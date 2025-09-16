using System;

namespace Miningcore.Blockchain.Kaspa;

public enum KaspaNetwork
{
    Mainnet,
    Testnet,
    Simnet,
    Devnet
}

public static class KaspaNetworkExtensions
{
    private static readonly byte[] MainnetExtendedPrivateKeyPrefix = {0x03, 0x8f, 0x2e, 0xf4};
    private static readonly byte[] TestnetExtendedPrivateKeyPrefix = {0x03, 0x90, 0x9e, 0x07};

    public static KaspaNetwork ParseNetworkId(string networkId)
    {
        if(string.IsNullOrWhiteSpace(networkId))
            return KaspaNetwork.Mainnet;

        var normalized = networkId.Trim().ToLowerInvariant();

        if(normalized.StartsWith("kaspa-"))
            normalized = normalized[6..];
        if(normalized.StartsWith("kaspa"))
            normalized = normalized[5..];

        if(normalized.Contains("sim"))
            return KaspaNetwork.Simnet;
        if(normalized.Contains("dev"))
            return KaspaNetwork.Devnet;
        if(normalized.Contains("test"))
            return KaspaNetwork.Testnet;

        return KaspaNetwork.Mainnet;
    }

    public static string GetAddressPrefix(this KaspaNetwork network)
    {
        return network switch
        {
            KaspaNetwork.Mainnet => "kaspa",
            KaspaNetwork.Testnet => "kaspatest",
            KaspaNetwork.Simnet => "kaspasim",
            KaspaNetwork.Devnet => "kaspadev",
            _ => "kaspa"
        };
    }

    public static ReadOnlySpan<byte> GetExtendedPrivateKeyPrefix(this KaspaNetwork network)
    {
        return network == KaspaNetwork.Mainnet
            ? MainnetExtendedPrivateKeyPrefix
            : TestnetExtendedPrivateKeyPrefix;
    }
}
