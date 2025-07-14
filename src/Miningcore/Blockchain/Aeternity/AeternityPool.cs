using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Autofac;
using AutoMapper;
using Microsoft.IO;
using Miningcore.Blockchain.Aeternity;
using Miningcore.Blockchain.Ethereum;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using NLog;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.Aeternity;

[CoinFamily(CoinFamily.Aeternity)]
public class AeternityPool : PoolBase
{
    public AeternityPool(IComponentContext ctx,
        JsonSerializerSettings serializerSettings,
        IConnectionFactory cf,
        IStatsRepository statsRepo,
        IMapper mapper,
        IMasterClock clock,
        IMessageBus messageBus,
        RecyclableMemoryStreamManager rmsm,
        NicehashService nicehashService) :
        base(ctx, serializerSettings, cf, statsRepo, mapper, clock, messageBus, rmsm, nicehashService)
    {
    }

    private object currentJobParams;
    private AeternityJobManager manager;
    private AeternityCoinTemplate coin;

    protected virtual async Task OnAuthorizeAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<AeternityWorkerContext>();

        if (request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        var requestParams = request.ParamsAs<string[]>();
        var workerValue = requestParams?.Length > 0 ? requestParams[0] : null;
        var password = requestParams?.Length > 1 ? requestParams[1] : null;
        var passParts = password?.Split(PasswordControlVarsSeparator);

        // extract worker/miner
        var split = workerValue?.Split('.');
        var minerName = split?.FirstOrDefault()?.Trim();
        var workerName = split?.Skip(1).FirstOrDefault()?.Trim() ?? string.Empty;

        // assumes that workerName is the BTC address
        context.IsAuthorized = await manager.ValidateAddressAsync(minerName, ct);
        context.Miner = minerName;
        context.Worker = workerName;

        if (context.IsAuthorized)
        {
            // respond
            await connection.RespondAsync(true, request.Id);

            // log association
            logger.Info(() => $"[{connection.ConnectionId}] Authorized worker {workerValue}");

            // extract control vars from password
            var staticDiff = GetStaticDiffFromPassparts(passParts);
            
            // Static diff
            if (staticDiff.HasValue &&
               (context.VarDiff != null && staticDiff.Value >= context.VarDiff.Config.MinDiff ||
                   context.VarDiff == null && staticDiff.Value > context.Difficulty))
            {
                context.VarDiff = null; // disable vardiff
                context.SetDifficulty(staticDiff.Value);

                logger.Info(() => $"[{connection.ConnectionId}] Setting static difficulty of {staticDiff.Value}");
            }
        }

        else
        {
            await connection.RespondErrorAsync(StratumError.UnauthorizedWorker, "Authorization failed", request.Id, context.IsAuthorized);

            if (clusterConfig?.Banning?.BanOnLoginFailure is null or true)
            {
                logger.Info(() => $"[{connection.ConnectionId}] Banning unauthorized worker {minerName} for {loginFailureBanTimeout.TotalSeconds} sec");

                banManager.Ban(connection.RemoteEndpoint.Address, loginFailureBanTimeout);

                Disconnect(connection);
            }
        }
    }

    protected virtual async Task OnSubscribeAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<AeternityWorkerContext>();

        if (request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        var requestParams = request.ParamsAs<string[]>();

        if (requestParams == null || requestParams.Length < 2)
            throw new StratumException(StratumError.MinusOne, "invalid request");

        manager.PrepareWorker(connection);

        // setup worker context
        context.IsSubscribed = true;
        context.UserAgent = requestParams.Length > 0 ? requestParams[0].Trim() : null;

        // send response
        await connection.RespondAsync(new object[]
        {
            context.ExtraNonce1
        }, request.Id);

        // send initial update
        await connection.NotifyAsync(AeternityStratumMethods.SetDifficulty, new object[] { context.Difficulty });
        await connection.NotifyAsync(AeternityStratumMethods.MiningNotify, currentJobParams);
    }

    protected virtual async Task OnSubmitAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<AeternityWorkerContext>();

        try
        {
            if (request.Id == null)
                throw new StratumException(StratumError.MinusOne, "missing request id");

            // check age of submission (aged submissions are usually caused by high server load)
            var requestAge = clock.Now - tsRequest.Timestamp.UtcDateTime;

            if (requestAge > maxShareAge)
            {
                logger.Warn(() => $"[{connection.ConnectionId}] Dropping stale share submission request (server overloaded?)");
                return;
            }

            // check worker state
            context.LastActivity = clock.Now;

            // validate worker
            if (!context.IsAuthorized)
                throw new StratumException(StratumError.UnauthorizedWorker, "unauthorized worker");
            else if (!context.IsSubscribed)
                throw new StratumException(StratumError.NotSubscribed, "not subscribed");

            var requestParams = request.ParamsAs<string[]>();
            var share = await manager.SubmitShareAsync(connection, requestParams, context.Difficulty, ct);

            await connection.RespondAsync(true, request.Id);

            // publish
            messageBus.SendMessage(share);

            // telemetry
            PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, true);

            logger.Info(() => $"[{connection.ConnectionId}] Share accepted: D={Math.Round(share.Difficulty, 3)}");

            // update pool stats
            if (share.IsBlockCandidate)
                poolStats.LastPoolBlockTime = clock.Now;

            // update client stats
            context.Stats.ValidShares++;

            await UpdateVarDiffAsync(connection, false, ct);
        }

        catch (StratumException ex)
        {
            // telemetry
            PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, false);

            // update client stats
            context.Stats.InvalidShares++;
            logger.Info(() => $"[{connection.ConnectionId}] Share rejected: {ex.Message} [{context.UserAgent}]");

            // banning
            ConsiderBan(connection, context, poolConfig.Banning);

            throw;
        }
    }

    private object CreateWorkerJob(StratumConnection connection, bool cleanJob)
    {
        var context = connection.ContextAs<AeternityWorkerContext>();
        var job = manager.GetJobForStratum();

        if (job != null)
        {
            var jobParams = manager.GetJobParamsForStratum();

            if (jobParams != null)
            {
                return jobParams;
            }
        }

        return null;
    }

    #region Overrides

    protected override async Task SetupJobManager(CancellationToken ct)
    {
        manager = ctx.Resolve<AeternityJobManager>();
        manager.Configure(poolConfig, clusterConfig);

        await manager.StartAsync(ct);

        if (poolConfig.EnableInternalStratum == true)
        {
            disposables.Add(manager.Jobs
                .Select(x => Observable.FromAsync(() =>
                    Guard(()=> OnNewJobAsync(x),
                        ex => logger.Debug(() => $"{nameof(OnNewJobAsync)}: {ex.Message}"))))
                .Concat()
                .Subscribe(_ => { }, ex =>
                {
                    logger.Debug(ex, nameof(OnNewJobAsync));
                }));

            // Wait for first job instead of calling immediately
            logger.Info(() => "Pool waiting for first job from job manager...");
        }

        else
        {
            // keep updating NetworkStats
            disposables.Add(manager.Jobs.Subscribe());
        }
    }

    protected virtual async Task OnNewJobAsync(object jobParams)
    {
        currentJobParams = CreateWorkerJob(null, true);

        if (currentJobParams == null)
        {
            currentJobParams = manager.GetJobParamsForStratum();
        }

        if (currentJobParams == null)
        {
            logger.Debug(() => "No job parameters available, skipping job notification");
            return;
        }

        // update connected workers
        foreach(var kvp in connections.ToArray())
        {
            var connection = kvp.Value;
            var context = connection.ContextAs<AeternityWorkerContext>();
            
            if (context?.IsSubscribed == true)
            {
                // check if we should send a new job (clean or not)
                var cleanJob = context.IsInitialWorkSent == false;

                if (!cleanJob)
                    cleanJob = true; // always send clean job for Aeternity

                try
                {
                    // send job
                    await connection.NotifyAsync(AeternityStratumMethods.MiningNotify, currentJobParams);
                    context.IsInitialWorkSent = true;
                }
                catch (Exception ex)
                {
                    logger.Debug(() => $"Failed to notify connection {connection.ConnectionId}: {ex.Message}");
                }
            }
        }
    }

    public override double HashrateFromShares(double shares, double interval)
    {
        var result = shares / interval;
        return result;
    }

    public override double ShareMultiplier => 1;

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        coin = pc.Template.As<AeternityCoinTemplate>();

        base.Configure(pc, cc);
    }

    protected override async Task OnRequestAsync(StratumConnection connection,
        Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;

        try
        {
            switch (request.Method)
            {
                case AeternityStratumMethods.Subscribe:
                    await OnSubscribeAsync(connection, tsRequest);
                    break;

                case AeternityStratumMethods.Authorize:
                    await OnAuthorizeAsync(connection, tsRequest, ct);
                    break;

                case AeternityStratumMethods.Submit:
                    await OnSubmitAsync(connection, tsRequest, ct);
                    break;

                case AeternityStratumMethods.SetExtraNonce:
                    // Not implemented for Aeternity
                    await connection.RespondErrorAsync(StratumError.Other, "not supported", request.Id, false);
                    break;

                default:
                    logger.Debug(() => $"[{connection.ConnectionId}] Unsupported RPC request: {JsonConvert.SerializeObject(request, serializerSettings)}");

                    await connection.RespondErrorAsync(StratumError.Other, $"Unsupported request {request.Method}", request.Id);
                    break;
            }
        }

        catch (StratumException ex)
        {
            await connection.RespondErrorAsync(ex.Code, ex.Message, request.Id, false);
        }
    }

    protected override async Task OnVarDiffUpdateAsync(StratumConnection connection, double newDiff, CancellationToken ct)
    {
        await connection.NotifyAsync(AeternityStratumMethods.SetDifficulty, new object[] { newDiff });
    }

    protected override async Task InitStatsAsync(CancellationToken ct)
    {
        await base.InitStatsAsync(ct);

        blockchainStats = manager.BlockchainStats;
    }

    protected override WorkerContextBase CreateWorkerContext()
    {
        return new AeternityWorkerContext();
    }

    #endregion
}
