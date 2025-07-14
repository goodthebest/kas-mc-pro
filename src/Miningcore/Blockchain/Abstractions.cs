namespace Miningcore.Blockchain;

public class BlockchainStats
{
    public string NetworkType { get; set; }
    public double NetworkHashrate { get; set; }
    public double NetworkDifficulty { get; set; }
    public string NextNetworkTarget { get; set; }
    public string NextNetworkBits { get; set; }
    public DateTime? LastNetworkBlockTime { get; set; }
    public ulong BlockHeight { get; set; }
    public int ConnectedPeers { get; set; }
    public string NodeVersion { get; set; } = "Unknown";
    public string RewardType { get; set; }
    
    /// <summary>
    /// Blockchain synchronization status
    /// </summary>
    public bool? IsSyncing { get; set; }
    
    /// <summary>
    /// Blockchain synchronization progress (0.0 to 1.0)
    /// </summary>
    public double? SyncProgress { get; set; }
    
    /// <summary>
    /// Current blockchain download percentage (0.0 to 100.0)
    /// </summary>
    public double? BlockDownloadProgress { get; set; }
}

public interface IExtraNonceProvider
{
    int ByteSize { get; }
    string Next();
}
