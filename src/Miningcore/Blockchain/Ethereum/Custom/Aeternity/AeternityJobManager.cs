using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using System.Text;
using Autofac;
using Miningcore.Blockchain.Ethereum.Custom.Aeternity.DaemonResponses;
using Miningcore.Blockchain.Ethereum.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Crypto.Hashing.Ethash;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Rest;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using NLog;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.Ethereum.Custom.Aeternity;

public class AeternityJobManager : JobManagerBase<AeternityJob>
{
    public AeternityJobManager(
        IComponentContext ctx,
        IMasterClock clock,
        IMessageBus messageBus,
        IExtraNonceProvider extraNonceProvider,
        IHttpClientFactory httpClientFactory) :
        base(ctx, messageBus)
    {
        this.clock = clock;
        this.extraNonceProvider = extraNonceProvider;
        this.httpClientFactory = httpClientFactory;
    }

    private EthereumCoinTemplate coin;
    private DaemonEndpointConfig[] daemonEndpoints;
    private SimpleRestClient restClient;
    private readonly IMasterClock clock;
    private readonly IExtraNonceProvider extraNonceProvider;
    private readonly IHttpClientFactory httpClientFactory;
    private EthereumPoolConfigExtra extraPoolConfig;
    private AeternityJob currentJob;

    private async Task<AeternityBlockTemplate> GetBlockTemplateAsync(CancellationToken ct)
    {
        try
        {
            // Get current keyblock (header info)
            var keyBlock = await restClient.Get<AeternityKeyBlock>("key-blocks/current", ct);
            if (keyBlock == null)
                return null;

            // Get pending keyblock template
            var pendingKeyBlock = await restClient.Get<AeternityKeyBlock>("key-blocks/pending", ct);
            if (pendingKeyBlock == null)
                return null;

            // Calculate target from difficulty
            var target = AeternityUtils.DifficultyToTarget(pendingKeyBlock.Target);

            var result = new AeternityBlockTemplate
            {
                Header = pendingKeyBlock.Hash,
                Height = pendingKeyBlock.Height,
                Target = target.ToHexString(true),
                Difficulty = pendingKeyBlock.Target,
                PreviousBlockHash = pendingKeyBlock.PrevHash,
                Version = pendingKeyBlock.Version,
                Time = pendingKeyBlock.Time,
                Nonce = pendingKeyBlock.Nonce ?? 0,
                Beneficiary = pendingKeyBlock.Beneficiary
            };

            return result;
        }
        catch (Exception ex)
        {
            logger.Error($"Error getting block template: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> SubmitBlockAsync(Share share, string nonce, string solution)
    {
        try
        {
            var submitData = new AeternityBlockSubmission
            {
                Nonce = nonce,
                Solution = solution,
                KeyBlock = currentJob.BlockTemplate.Header
            };

            var response = await restClient.PostWithResponse<object>("key-blocks", submitData, CancellationToken.None);

            if (response.Response.IsSuccessStatusCode)
            {
                logger.Info(() => $"Successfully submitted block {share.BlockHeight}");
                return true;
            }
            else
            {
                var errorContent = await response.Response.Content.ReadAsStringAsync();
                logger.Warn(() => $"Block {share.BlockHeight} submission failed: {errorContent}");
                messageBus.SendMessage(new AdminNotification("Block submission failed", 
                    $"Pool {poolConfig.Id} failed to submit block {share.BlockHeight}: {errorContent}"));
                return false;
            }
        }
        catch (Exception ex)
        {
            logger.Error($"Block submission error: {ex.Message}");
            return false;
        }
    }

    protected async Task<bool> UpdateJob(CancellationToken ct, string via = null)
    {
        try
        {
            var blockTemplate = await GetBlockTemplateAsync(ct);
            
            if (blockTemplate == null)
                return false;

            var isNew = currentJob == null ||
                currentJob.BlockTemplate.Height < blockTemplate.Height ||
                currentJob.BlockTemplate.Header != blockTemplate.Header;

            if (isNew)
            {
                messageBus.NotifyChainHeight(poolConfig.Id, blockTemplate.Height, poolConfig.Template);

                var jobId = NextJobId("x8");

                // Create Aeternity-specific job
                currentJob = new AeternityJob(jobId, blockTemplate, logger, null);

                logger.Info(() => $"New Aeternity work at height {currentJob.BlockTemplate.Height} via [{via ?? "Unknown"}]");

                // Emit job
                Jobs.OnNext(Unit.Default);

                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            logger.Error($"Update job error: {ex.Message}");
            return false;
        }
    }

    private async Task ShowDaemonSyncProgressAsync(CancellationToken ct)
    {
        try
        {
            var status = await restClient.Get<AeternityNodeStatus>("status", ct);
            
            if (status?.Syncing == true)
            {
                logger.Info(() => $"Daemon is syncing: {status.SyncProgress?.ToFixed(2) ?? "unknown"}% complete");
            }
        }
        catch (Exception ex)
        {
            logger.Debug($"Sync progress check failed: {ex.Message}");
        }
    }

    private async Task UpdateNetworkStatsAsync(CancellationToken ct)
    {
        try
        {
            var keyBlock = await restClient.Get<AeternityKeyBlock>("key-blocks/current", ct);
            
            if (keyBlock != null)
            {
                BlockchainStats.NetworkHashrate = AeternityUtils.CalculateNetworkHashrate(keyBlock.Target);
                BlockchainStats.ConnectedPeers = 0; // Aeternity doesn't expose peer count in REST API
            }
        }
        catch (Exception ex)
        {
            logger.Debug($"Network stats update failed: {ex.Message}");
        }
    }

    #region API-Surface

    public IObservable<Unit> Jobs { get; private set; }
    public BlockchainStats BlockchainStats { get; } = new();

    public bool ValidateAddress(string address)
    {
        return !string.IsNullOrEmpty(address) && address.StartsWith("ak_") && address.Length >= 50;
    }

    public void PrepareWorker(StratumConnection client)
    {
        var context = client.ContextAs<EthereumWorkerContext>();
        context.ExtraNonce1 = extraNonceProvider.Next();
    }

    public async Task<Share> SubmitShareAsync(StratumConnection worker, string[] request, CancellationToken ct)
    {
        var context = worker.ContextAs<EthereumWorkerContext>();
        var workerName = request[0];
        var jobId = request[1];
        var nonce = request[2];
        var solution = request[3];

        var job = currentJob;

        if (job?.Id != jobId)
            throw new StratumException(StratumError.MinusOne, "stale share");

        var share = await job.ProcessShareAsync(worker, workerName, nonce, solution, ct);

        if (share.Share.IsBlockCandidate)
        {
            await SubmitBlockAsync(share.Share, nonce, solution);
        }

        return share.Share;
    }

    public override AeternityJob GetJobForStratum()
    {
        return currentJob;
    }

    public object[] GetJobParamsForStratum()
    {
        var job = currentJob;
        return job?.GetJobParamsForStratum();
    }

    #endregion

    #region Overrides

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        coin = pc.Template.As<EthereumCoinTemplate>();
        extraPoolConfig = pc.Extra.SafeExtensionDataAs<EthereumPoolConfigExtra>();

        // Extract daemon endpoints
        daemonEndpoints = pc.Daemons
            .Where(x => string.IsNullOrEmpty(x.Category))
            .ToArray();

        base.Configure(pc, cc);

        // Initialize Jobs observable
        Jobs = blockFoundSubject.Select(_ => Unit.Default)
            .Publish()
            .RefCount();
    }

    protected override void ConfigureDaemons()
    {
        var endpoint = daemonEndpoints.First();
        var baseUrl = $"http://{endpoint.Host}:{endpoint.Port}/v3/";
        
        restClient = new SimpleRestClient(httpClientFactory, baseUrl);
        
        logger.Info(() => $"Configured Aeternity REST client for {baseUrl}");
    }

    protected override async Task<bool> AreDaemonsHealthyAsync(CancellationToken ct)
    {
        try
        {
            var status = await restClient.Get<AeternityNodeStatus>("status", ct);
            return status != null;
        }
        catch (Exception ex)
        {
            logger.Error(() => $"Daemon health check failed: {ex.Message}");
            return false;
        }
    }

    protected override async Task<bool> AreDaemonsConnectedAsync(CancellationToken ct)
    {
        return await AreDaemonsHealthyAsync(ct);
    }

    protected override async Task EnsureDaemonsSynchedAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        var syncPendingNotificationShown = false;

        do
        {
            var status = await restClient.Get<AeternityNodeStatus>("status", ct);

            var isSynched = status?.Syncing != true;

            if (isSynched)
            {
                logger.Info(() => "Aeternity daemon synched with blockchain");
                break;
            }

            if (!syncPendingNotificationShown)
            {
                logger.Info(() => "Aeternity daemon is still syncing with network. Manager will be started once synced.");
                syncPendingNotificationShown = true;
            }

            await ShowDaemonSyncProgressAsync(ct);
        } while (await timer.WaitForNextTickAsync(ct));
    }

    protected override async Task PostStartInitAsync(CancellationToken ct)
    {
        // Validate node connection and get network info
        var status = await restClient.Get<AeternityNodeStatus>("status", ct);
        
        if (status == null)
            throw new PoolStartupException("Failed to connect to Aeternity node", poolConfig.Id);

        // Update stats
        BlockchainStats.RewardType = "POW";
        BlockchainStats.NetworkType = $"Aeternity-{status.NetworkId ?? "unknown"}";

        await UpdateNetworkStatsAsync(ct);

        // Periodically update network stats
        Observable.Interval(TimeSpan.FromMinutes(10))
            .Select(via => Observable.FromAsync(() =>
                Guard(() => UpdateNetworkStatsAsync(ct),
                    ex => logger.Error(ex))))
            .Concat()
            .Subscribe();

        // Start job updates
        await SetupJobUpdates(ct);
    }

    private async Task SetupJobUpdates(CancellationToken ct)
    {
        var pollingInterval = poolConfig.BlockRefreshInterval > 0 ? poolConfig.BlockRefreshInterval : 3000; // Default 3 seconds for Aeternity

        var triggers = new List<IObservable<(string Via, string Data)>>
        {
            blockFoundSubject.Select(_ => (JobRefreshBy.BlockFound, (string) null))
        };

        // Add polling trigger since Aeternity doesn't have WebSocket subscriptions like Ethereum
        triggers.Add(Observable.Timer(TimeSpan.Zero, TimeSpan.FromMilliseconds(pollingInterval))
            .Select(_ => (JobRefreshBy.Poll, (string) null))
            .TakeUntil(DisposeCancellationToken));

        Jobs = triggers.Merge()
            .Select(x => Observable.FromAsync(() => Guard(() => UpdateJob(ct, x.Via),
                ex => logger.Error(ex))))
            .Concat()
            .Where(x => x)
            .Select(_ => Unit.Default)
            .Publish()
            .RefCount();

        Jobs.Subscribe();
    }

    #endregion
}
