using Miningcore.Mining;

namespace Miningcore.Blockchain.Aeternity;

public class AeternityWorkerContext : WorkerContextBase
{
    /// <summary>
    /// Usually a wallet address
    /// </summary>
    public override string Miner { get; set; }

    /// <summary>
    /// Arbitrary worker identififer for miners using multiple rigs
    /// </summary>
    public override string Worker { get; set; }

    /// <summary>
    /// Unique value assigned per worker
    /// </summary>
    public string ExtraNonce1 { get; set; }

    /// <summary>
    /// Track if initial work was sent to this worker
    /// </summary>
    public bool IsInitialWorkSent { get; set; }

    /// <summary>
    /// Current job assigned to this worker
    /// </summary>
    public AeternityJob CurrentJob { get; set; }
}
