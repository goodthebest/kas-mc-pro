using System;
using System.Linq;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace Miningcore.Blockchain.Kaspa;

public static class KaspaTreasuryKeyDeriver
{
    public const string DefaultDerivationPath = "m/44'/972/0'/0/0";

    public static KaspaDerivedKey DeriveFromMnemonic(string mnemonic, string derivationPath, KaspaNetwork network)
    {
        if(string.IsNullOrWhiteSpace(mnemonic))
            throw new ArgumentException("Mnemonic must be provided", nameof(mnemonic));

        var normalizedPath = string.IsNullOrWhiteSpace(derivationPath) ? DefaultDerivationPath : derivationPath;
        var trimmedMnemonic = mnemonic.Trim();
        var bip39 = new Mnemonic(trimmedMnemonic, Wordlist.English);
        var seed = bip39.DeriveSeed();
        var rootKey = ExtKey.CreateFromSeed(seed);
        return Derive(rootKey, normalizedPath, network);
    }

    public static KaspaDerivedKey DeriveFromSeed(string seedHex, string derivationPath, KaspaNetwork network)
    {
        if(string.IsNullOrWhiteSpace(seedHex))
            throw new ArgumentException("Seed must be provided", nameof(seedHex));

        var normalizedPath = string.IsNullOrWhiteSpace(derivationPath) ? DefaultDerivationPath : derivationPath;
        var seedBytes = Encoders.Hex.DecodeData(seedHex.Trim());
        var rootKey = ExtKey.CreateFromSeed(seedBytes);
        return Derive(rootKey, normalizedPath, network);
    }

    private static KaspaDerivedKey Derive(ExtKey rootKey, string derivationPath, KaspaNetwork network)
    {
        if(rootKey == null)
            throw new ArgumentNullException(nameof(rootKey));

        var keyPath = KeyPath.Parse(derivationPath);
        var derivedKey = rootKey.Derive(keyPath);

        var privateKeyBytes = derivedKey.PrivateKey.ToBytes();
        var privateKeyHex = Encoders.Hex.EncodeData(privateKeyBytes).ToLowerInvariant();

        var publicKeyBytes = derivedKey.PrivateKey.PubKey.ToBytes();
        var publicKeyHex = Encoders.Hex.EncodeData(publicKeyBytes).ToLowerInvariant();

        var xOnlyPublicKeyBytes = publicKeyBytes.Skip(1).ToArray();
        var xOnlyPublicKeyHex = Encoders.Hex.EncodeData(xOnlyPublicKeyBytes).ToLowerInvariant();

        var extendedPrivateKey = EncodeExtendedPrivateKey(derivedKey, network);
        var address = KaspaBech32Encoder.EncodeAddress(network, 0, xOnlyPublicKeyBytes);

        return new KaspaDerivedKey(
            extendedPrivateKey,
            privateKeyHex,
            publicKeyHex,
            xOnlyPublicKeyHex,
            address);
    }

    private static string EncodeExtendedPrivateKey(ExtKey key, KaspaNetwork network)
    {
        var version = network.GetExtendedPrivateKeyPrefix();
        Span<byte> buffer = stackalloc byte[78];

        version.CopyTo(buffer);
        buffer[4] = key.Depth;

        var fingerprint = key.ParentFingerprint.ToBytes();
        fingerprint.CopyTo(buffer[5..9]);

        var child = key.Child;
        buffer[9] = (byte) ((child >> 24) & 0xff);
        buffer[10] = (byte) ((child >> 16) & 0xff);
        buffer[11] = (byte) ((child >> 8) & 0xff);
        buffer[12] = (byte) (child & 0xff);

        key.ChainCode.CopyTo(buffer[13..45]);
        buffer[45] = 0x00;

        var privateKeyBytes = key.PrivateKey.ToBytes();
        privateKeyBytes.CopyTo(buffer[46..78]);

        return Encoders.Base58Check.EncodeData(buffer.ToArray());
    }
}

public class KaspaDerivedKey
{
    public KaspaDerivedKey(string extendedPrivateKey, string privateKeyHex, string publicKeyHex, string xOnlyPublicKeyHex, string address)
    {
        ExtendedPrivateKey = extendedPrivateKey;
        PrivateKeyHex = privateKeyHex;
        PublicKeyHex = publicKeyHex;
        XOnlyPublicKeyHex = xOnlyPublicKeyHex;
        Address = address;
    }

    public string ExtendedPrivateKey { get; }
    public string PrivateKeyHex { get; }
    public string PublicKeyHex { get; }
    public string XOnlyPublicKeyHex { get; }
    public string Address { get; }
}
