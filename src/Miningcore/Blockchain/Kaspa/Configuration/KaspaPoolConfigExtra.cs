using Miningcore.Configuration;

namespace Miningcore.Blockchain.Kaspa.Configuration;

public class KaspaPoolConfigExtra
{
    /// <summary>
    /// There are several reports of IDIOTS mining with ridiculous amount of hashrate and maliciously using a very low staticDiff in order to attack mining pools.
    /// StaticDiff is now disabled by default for the KASPA family. Use it at your own risks.
    /// </summary>
    public bool EnableStaticDifficulty { get; set; } = false;

    /// <summary>
    /// Maximum number of tracked jobs.
    /// Default: 8
    /// </summary>
    public int? MaxActiveJobs { get; set; }
    
    /// <summary>
    /// Arbitrary string added in the Kaspa coinbase tx
    /// Default: "Miningcore.developers["Cedric CRISPIN"]"
    /// </summary>
    public string ExtraData { get; set; }

    public int? ExtraNonce1Size { get; set; }

    /// <summary>
    /// Optional BIP39 mnemonic used to derive the treasury extended private key and address.
    /// When set, <see cref="KaspaSeed"/> must be left empty.
    /// </summary>
    public string KaspaMnemonic { get; set; }

    /// <summary>
    /// Optional hex-encoded BIP39 seed used to derive the treasury extended private key and address.
    /// When set, <see cref="KaspaMnemonic"/> must be left empty.
    /// </summary>
    public string KaspaSeed { get; set; }

    /// <summary>
    /// Optional derivation path used when generating the treasury key material.
    /// Defaults to m/44'/972/0'/0/0 if not specified.
    /// </summary>
    public string KaspaDerivationPath { get; set; }

    /// <summary>
    /// When set to true, payout transactions will be submitted with the kaspad
    /// orphan allowance flag enabled. This should typically remain <c>false</c>
    /// unless explicitly required for a particular deployment.
    /// </summary>
    public bool AllowOrphanTransactions { get; set; }

    /// <summary>
    /// Optional: Daemon RPC service name override
    /// Should match the value of .proto file
    /// Default: "protowire.RPC"
    /// </summary>
    public string ProtobufDaemonRpcServiceName { get; set; }

    /// <summary>
    /// Optional set of Kaspa wRPC endpoints used by RustyKaspaWallet-enabled tooling.
    /// Each endpoint should reference a kaspad instance started with --utxoindex and the configured encoding.
    /// </summary>
    public KaspaWrpcEndpointConfig[] WrpcEndpoints { get; set; }
}

public class KaspaWrpcEndpointConfig : NetworkEndpointConfig
{
    /// <summary>
    /// Encoding advertised by the kaspad wRPC server (borsh or json).
    /// Default is "borsh" which matches kaspa-wrpc-client defaults.
    /// </summary>
    public string Encoding { get; set; } = "borsh";

    /// <summary>
    /// Optional flag indicating whether the endpoint requires TLS (wss).
    /// </summary>
    public bool UseTls { get; set; }
}
