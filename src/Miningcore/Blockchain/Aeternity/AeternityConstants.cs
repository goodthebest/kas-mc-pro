using Miningcore.Configuration;

namespace Miningcore.Blockchain.Aeternity;

public static class AeternityStratumMethods
{
    /// <summary>
    /// Used to subscribe to work from a server, required before all other communication.
    /// </summary>
    public const string Subscribe = "mining.subscribe";

    /// <summary>
    /// Used to authorize a worker, required before any shares can be submitted.
    /// </summary>
    public const string Authorize = "mining.authorize";

    /// <summary>
    /// Used to submit shares
    /// </summary>
    public const string Submit = "mining.submit";

    /// <summary>
    /// Used to signal the miner to stop submitting shares under the current block.
    /// </summary>
    public const string MiningNotify = "mining.notify";

    /// <summary>
    /// Used to suggest a difficulty to the miner.
    /// </summary>
    public const string SetDifficulty = "mining.set_difficulty";

    /// <summary>
    /// Used to negotiate to set a difficulty
    /// </summary>
    public const string SuggestDifficulty = "mining.suggest_difficulty";

    /// <summary>
    /// Used to negotiate to set a target
    /// </summary>
    public const string SuggestTarget = "mining.suggest_target";

    /// <summary>
    /// Used to negotiate to set a difficulty 
    /// </summary>
    public const string SetTarget = "mining.set_target";

    /// <summary>
    /// Used by pools to notify clients about pool 
    /// </summary>
    public const string SetExtraNonce = "mining.set_extranonce";
}

public class AeternityPoolConfig : PoolConfig
{
    public new AeternityCoinTemplate Template { get; set; }
}
