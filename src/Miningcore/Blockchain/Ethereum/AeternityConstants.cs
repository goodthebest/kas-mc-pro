using System;
using System.Numerics;

namespace Miningcore.Blockchain.Ethereum;

// Aeternity block reward and network constants
// Based on Aeternity protocol specifications
public class AeternityConstants
{
    public static double Pow2x30 = Math.Pow(2, 30);
    public static BigInteger BigPow2x30 = new(Pow2x30);

    // Aeternity uses Cuckoo Cycle with different parameters than Cortex
    public const int CuckarooHeaderNonceSize = 40;
    public const int CuckarooSolutionSize = 42;
    
    // Aeternity-specific parameters
    public const int EdgeBits = 29;  // Aeternity uses 29-bit edges
    public const int ProofSize = 42; // Same as Cortex
    
    // Difficulty adjustment
    public const int BlockTime = 180; // 3 minutes in seconds
    public const int DifficultyAdjustmentWindow = 17; // blocks
}
