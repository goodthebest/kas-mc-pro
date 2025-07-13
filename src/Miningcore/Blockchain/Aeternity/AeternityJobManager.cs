using System.Globalization;
using System.Numerics;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using Autofac;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Blockchain.Ethereum;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using Contract = Miningcore.Contracts.Contract;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.Aeternity;

public class AeternityJobManager : JobManagerBase<AeternityJob>
{
    public AeternityJobManager(
        IComponentContext ctx,
        IMasterClock clock,
        IMessageBus messageBus) : base(ctx, messageBus)
    {
        Contract.RequiresNonNull(ctx);
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(messageBus);

        this.clock = clock;
        httpClient = new HttpClient();
    }

    private readonly IMasterClock clock;
    private readonly Dictionary<string, AeternityJob> validJobs = new();
    private readonly Subject<Unit> jobSubject = new();
    private readonly HttpClient httpClient;
    private string nodeUrl;
    
    public IObservable<Unit> Jobs { get; private set; }
    
    #region API-Surface

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        base.Configure(pc, cc);

        var aeternityCoinTemplate = poolConfig.Template.As<AeternityCoinTemplate>();

        // Configure node URL from daemon settings
        var daemon = pc.Daemons?.FirstOrDefault();
        if (daemon != null)
        {
            nodeUrl = $"http://{daemon.Host}:{daemon.Port}";
            logger.Info(() => $"Configured Aeternity node URL: {nodeUrl}");
        }
        else
        {
            throw new PoolStartupException("No Aeternity daemon configured", poolConfig.Id);
        }

        // Initialize Jobs observable
        Jobs = jobSubject.AsObservable();

        ConfigureDaemons();
    }

    public string[] GetJobParamsForStratum()
    {
        var job = currentJob;
        if (job != null)
        {
            return new[]
            {
                job.JobId,
                job.HeaderHash,
                job.SeedHash,
                job.Target,
                "true" // Clean job
            };
        }

        return null;
    }

    public async Task<AeternityShare> SubmitShareAsync(StratumConnection worker,
        object submission, double stratumDifficulty, CancellationToken ct)
    {
        var submissionParams = submission as object[];

        if (submissionParams?.Length < 3)
            throw new StratumException(StratumError.Other, "invalid params");

        var jobId = submissionParams[1] as string;
        var nonce = submissionParams[2] as string;

        if (string.IsNullOrEmpty(jobId) || string.IsNullOrEmpty(nonce))
            throw new StratumException(StratumError.Other, "invalid params");

        AeternityJob job;

        lock (jobLock)
        {
            if (!validJobs.TryGetValue(jobId, out job))
                throw new StratumException(StratumError.JobNotFound, "job not found");
        }

        // validate nonce
        if (!AeternityUtils.IsValidNonce(nonce))
            throw new StratumException(StratumError.Other, "invalid nonce");

        // validate solution
        var nonceBytes = AeternityUtils.HexToByteArray(nonce);
        var hashBytes = AeternityUtils.HashCuckooSolution(job.HeaderHash, nonceBytes);
        
        var hashBigInt = new BigInteger(hashBytes.Reverse().ToArray());
        var targetBigInt = new BigInteger(AeternityUtils.HexToByteArray(job.Target).Reverse().ToArray());

        var stratumShare = new AeternityShare
        {
            JobId = jobId,
            Nonce = nonce,
            IsBlockCandidate = hashBigInt <= targetBigInt
        };

        // check if share meets stratum difficulty
        var shareDifficulty = CalculateShareDifficulty(hashBigInt);
        stratumShare.StratumDifficulty = stratumDifficulty;
        stratumShare.Difficulty = shareDifficulty;

        if (shareDifficulty < stratumDifficulty)
            throw new StratumException(StratumError.LowDifficultyShare, "low difficulty share");

        return stratumShare;
    }

    public void PrepareWorker(StratumConnection connection)
    {
        // Basic worker preparation
        var context = connection.ContextAs<AeternityWorkerContext>();
        if (context != null)
        {
            context.IsSubscribed = true;
        }
    }

    public BlockchainStats BlockchainStats { get; set; } = new BlockchainStats();

    #endregion // API-Surface

    #region Overrides

    protected override void ConfigureDaemons()
    {
        // For REST API based blockchain, no daemons to configure
    }

    public async Task<bool> ValidateAddressAsync(string address, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(address))
            return false;

        // Aeternity address validation - basic format check
        if (address.StartsWith("ak_") && address.Length == 51)
            return true;

        return false;
    }

    protected override async Task<bool> AreDaemonsHealthyAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(nodeUrl))
            return false;

        try
        {
            var response = await httpClient.GetAsync($"{nodeUrl}/v3/status", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.Debug(() => $"Aeternity node health check failed: {ex.Message}");
            return false;
        }
    }

    protected override async Task<bool> AreDaemonsConnectedAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(nodeUrl))
            return false;

        try
        {
            var response = await httpClient.GetAsync($"{nodeUrl}/v3/status", ct);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var status = JsonConvert.DeserializeObject<JObject>(content);
                var syncing = status?["syncing"]?.Value<bool>() ?? true;
                var nodeVersion = status?["node_version"]?.Value<string>();
                var topHeight = status?["top_height"]?.Value<long>() ?? 0;
                
                logger.Info(() => $"Aeternity node status: version={nodeVersion}, height={topHeight}, syncing={syncing}");
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            logger.Debug(() => $"Aeternity node connection check failed: {ex.Message}");
            return false;
        }
    }

    protected override async Task EnsureDaemonsSynchedAsync(CancellationToken ct)
    {
        // For REST API, no sync check needed
    }

    protected override async Task PostStartInitAsync(CancellationToken ct)
    {
        await UpdateJobAsync(ct);
    }

    protected async Task<bool> UpdateJobAsync(CancellationToken ct)
    {
        try
        {
            logger.Debug(() => "Updating job from Aeternity node");

            if (string.IsNullOrEmpty(nodeUrl))
            {
                logger.Error(() => "Node URL not configured");
                return false;
            }

            // Get current top block
            var topResponse = await httpClient.GetAsync($"{nodeUrl}/v3/key-blocks/current", ct);
            if (!topResponse.IsSuccessStatusCode)
            {
                logger.Error(() => $"Failed to get current block: {topResponse.StatusCode}");
                return false;
            }

            var topContent = await topResponse.Content.ReadAsStringAsync();
            var topBlock = JsonConvert.DeserializeObject<JObject>(topContent);
            
            var height = topBlock?["height"]?.Value<long>() ?? 0;
            var hash = topBlock?["hash"]?.Value<string>();
            var target = topBlock?["target"]?.Value<string>();
            var prevHash = topBlock?["prev_hash"]?.Value<string>();

            if (string.IsNullOrEmpty(hash))
            {
                logger.Error(() => "Failed to parse block data");
                return false;
            }

            // Create new job
            var job = new AeternityJob
            {
                JobId = Guid.NewGuid().ToString("N")[..8],
                Height = height,
                HeaderHash = hash,
                SeedHash = prevHash ?? "0x" + "0".PadLeft(64, '0'),
                Target = target ?? "0x" + "00000fffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
                Difficulty = (ulong)CalculateDifficultyFromTarget(target),
                Created = clock.Now
            };

            lock (jobLock)
            {
                // Clean old jobs
                var cutoff = clock.Now.AddMinutes(-4);
                var toDelete = validJobs.Where(x => x.Value.Created < cutoff).ToArray();
                foreach (var entry in toDelete)
                    validJobs.Remove(entry.Key);

                // Add new job
                validJobs[job.JobId] = job;
                currentJob = job;
            }

            // Notify subscribers
            jobSubject.OnNext(Unit.Default);

            logger.Info(() => $"New job {job.JobId} at height {job.Height} with difficulty {job.Difficulty}");
            return true;
        }
        catch (Exception ex)
        {
            logger.Error(ex, () => "Error updating job");
            return false;
        }
    }

    private double CalculateDifficultyFromTarget(string target)
    {
        if (string.IsNullOrEmpty(target))
            return 1000; // Default difficulty

        try
        {
            // Remove 0x prefix if present
            if (target.StartsWith("0x"))
                target = target.Substring(2);

            // Parse as hex and calculate difficulty
            var targetBig = BigInteger.Parse(target, NumberStyles.HexNumber);
            if (targetBig == 0)
                return 1000;

            // Simple difficulty calculation (can be refined)
            var maxTarget = BigInteger.Parse("00000fffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", NumberStyles.HexNumber);
            var difficulty = (double)maxTarget / (double)targetBig;
            
            return Math.Max(difficulty, 1);
        }
        catch (Exception ex)
        {
            logger.Debug(() => $"Failed to calculate difficulty from target {target}: {ex.Message}");
            return 1000;
        }
    }

    public override AeternityJob GetJobForStratum()
    {
        return currentJob;
    }

    #endregion // Overrides

    #region Utility Methods

    private double CalculateShareDifficulty(BigInteger hashBigInt)
    {
        var maxTarget = BigInteger.Pow(2, 256);
        return (double)(maxTarget / hashBigInt);
    }

    #endregion // Utility Methods

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            httpClient?.Dispose();
            jobSubject?.Dispose();
        }
    }
}

public class AeternityJob
{
    public string JobId { get; set; }
    public long Height { get; set; }
    public string HeaderHash { get; set; }
    public string SeedHash { get; set; }
    public string Target { get; set; }
    public ulong Difficulty { get; set; }
    public DateTime Created { get; set; }
}

public class AeternityShare
{
    public string JobId { get; set; }
    public string Nonce { get; set; }
    public bool IsBlockCandidate { get; set; }
    public double StratumDifficulty { get; set; }
    public double Difficulty { get; set; }
    public decimal BlockReward { get; set; }
}

public static class AeternityUtils
{
    public static bool IsValidNonce(string nonce)
    {
        if (string.IsNullOrEmpty(nonce) || nonce.Length != 18) // 0x + 16 hex chars
            return false;

        if (!nonce.StartsWith("0x"))
            return false;

        return nonce.Substring(2).All(c => "0123456789abcdefABCDEF".Contains(c));
    }

    public static byte[] HexToByteArray(string hex)
    {
        if (hex.StartsWith("0x"))
            hex = hex.Substring(2);

        return Enumerable.Range(0, hex.Length)
                         .Where(x => x % 2 == 0)
                         .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                         .ToArray();
    }

    public static string ByteArrayToHex(byte[] bytes)
    {
        return "0x" + Convert.ToHexString(bytes).ToLower();
    }

    public static byte[] HashCuckooSolution(string headerHash, byte[] nonce)
    {
        // Simplified Cuckoo Cycle hash computation
        // In a real implementation, this would use the actual Cuckoo Cycle algorithm
        var combined = HexToByteArray(headerHash).Concat(nonce).ToArray();
        
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        return sha256.ComputeHash(combined);
    }
}
