namespace Miningcore.Blockchain.Alephium.Configuration;

public class AlephiumPaymentProcessingConfigExtra
{
    /// <summary>
    /// Name of the wallet to use
    /// </summary>
    public string WalletName { get; set; }
    
    /// <summary>
    /// Password for unlocking wallet
    /// </summary>
    public string WalletPassword { get; set; }

    /// <summary>
    /// Minimum block confirmations
    /// </summary>
    public int? MinimumConfirmations { get; set; }
    
    /// <summary>
    /// True to exempt transaction fees from miner rewards
    /// </summary>
    public bool KeepTransactionFees { get; set; }
}