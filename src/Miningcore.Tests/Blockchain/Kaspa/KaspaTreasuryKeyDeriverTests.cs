using Miningcore.Blockchain.Kaspa;
using NBitcoin;
using NBitcoin.DataEncoders;
using Xunit;

namespace Miningcore.Tests.Blockchain.Kaspa;

public class KaspaTreasuryKeyDeriverTests
{
    [Fact]
    public void DerivesKnownKaspaKeyFromMnemonic()
    {
        const string mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

        var derived = KaspaTreasuryKeyDeriver.DeriveFromMnemonic(
            mnemonic,
            KaspaTreasuryKeyDeriver.DefaultDerivationPath,
            KaspaNetwork.Mainnet);

        Assert.Equal(
            "kprv69KonnpFRxMFJg92dsShntS8TUDANxfEMqSdqv8U9qmGFdfAXfTErjXwLo3qUCgNQWyNnLp5CErPZJ5Y4JEacUS4ExLRMYftQH3FBXyUdH5",
            derived.ExtendedPrivateKey);
        Assert.Equal(
            "53397ef426ef62f497eac3915fb2465edd9077650e67a50367d9f08ee7b9c0d1",
            derived.PrivateKeyHex);
        Assert.Equal(
            "a27527b7c1c2c7867134f956bf2bb3611f6abbc78413b870186225ae870f34cc",
            derived.XOnlyPublicKeyHex);
        Assert.Equal(
            "kaspa:qz382fahc8pv0pn3xnu4d0etkds3764mc7zp8wrsrp3ztt58pu6vclrs67rdl",
            derived.Address);
    }

    [Fact]
    public void SeedDerivationMatchesMnemonic()
    {
        const string mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
        var bip39 = new Mnemonic(mnemonic, Wordlist.English);
        var seedHex = Encoders.Hex.EncodeData(bip39.DeriveSeed());

        var fromMnemonic = KaspaTreasuryKeyDeriver.DeriveFromMnemonic(
            mnemonic,
            KaspaTreasuryKeyDeriver.DefaultDerivationPath,
            KaspaNetwork.Mainnet);
        var fromSeed = KaspaTreasuryKeyDeriver.DeriveFromSeed(
            seedHex,
            KaspaTreasuryKeyDeriver.DefaultDerivationPath,
            KaspaNetwork.Mainnet);

        Assert.Equal(fromMnemonic.ExtendedPrivateKey, fromSeed.ExtendedPrivateKey);
        Assert.Equal(fromMnemonic.PrivateKeyHex, fromSeed.PrivateKeyHex);
        Assert.Equal(fromMnemonic.PublicKeyHex, fromSeed.PublicKeyHex);
        Assert.Equal(fromMnemonic.XOnlyPublicKeyHex, fromSeed.XOnlyPublicKeyHex);
        Assert.Equal(fromMnemonic.Address, fromSeed.Address);
    }
}
