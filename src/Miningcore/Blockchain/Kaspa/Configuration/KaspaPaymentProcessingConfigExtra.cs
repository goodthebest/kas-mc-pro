namespace Miningcore.Blockchain.Kaspa.Configuration;

public class KaspaPaymentProcessingConfigExtra
{
    /// <summary>
    /// Minimum block confirmations
    /// KAS minimum confirmation can change over time so please always study all those different changes very wisely: https://github.com/kaspanet/rusty-kaspa/blob/master/wallet/core/src/utxo/settings.rs
    /// Default: (mainnet: 120, testnet: 110)
    /// </summary>
    public int? MinimumConfirmations { get; set; }
}