using System;
using System.Numerics;

namespace Miningcore.Blockchain.Ethereum;

// Aeternity block reward and network constants
// Based on Aeternity protocol specifications and stratum documentation
// https://docs.aeternity.io/en/stable/stratum/
public class AeternityConstants
{
    public static double Pow2x30 = Math.Pow(2, 30);
    public static BigInteger BigPow2x30 = new(Pow2x30);

    // Aeternity Cuckoo Cycle parameters (from official docs)
    public const int EdgeBits = 29;  // Aeternity uses 29-bit edges (confirmed from docs)
    public const int ProofSize = 42; // Standard Cuckoo Cycle proof size
    
    // Aeternity-specific stratum parameters
    public const int ExtraNonceBytes = 4; // Default from stratum config
    public const int TotalNonceBytes = 8; // Total nonce size (extra_nonce + miner_nonce)
    public const int CuckarooHeaderNonceSize = 40; // Header + nonce size
    public const int CuckarooSolutionSize = 42;     // Solution array size
    
    // Network parameters
    public const int BlockTime = 180; // 3 minutes in seconds (keyblock generation)
    public const int DifficultyAdjustmentWindow = 17; // blocks
    public const int BeneficiaryRewardDelay = 180; // epochs
    
    // Default stratum configuration values
    public const int DefaultPort = 9999;
    public const int DefaultDesiredSolveTime = 30; // seconds
    public const int DefaultMaxSolveTime = 60; // seconds
    public const double DefaultShareTargetDiffThreshold = 5.0; // percent
    
    // Initial and max share targets from documentation
    public static readonly BigInteger InitialShareTarget = BigInteger.Parse("115790322390251417039241401711187164934754157181743688420499462401711837019160");
    public static readonly BigInteger MaxShareTarget = BigInteger.Parse("115790322390251417039241401711187164934754157181743688420499462401711837020160");
}
