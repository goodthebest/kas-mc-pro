using System;
using System.Linq;
using Miningcore.Blockchain.Kaspa;
using Miningcore.Blockchain.Kaspa.Configuration;
using Miningcore.Blockchain.Kaspa.Wallet;
using Miningcore.Persistence.Model;
using Xunit;
using kaspad = Miningcore.Blockchain.Kaspa.Kaspad;

namespace Miningcore.Tests.Blockchain.Kaspa;

public class KaspaWalletTests
{
    private static KaspaCoinTemplate CreateCoinTemplate()
    {
        return new KaspaCoinTemplate
        {
            AddressBech32Prefix = "kaspa",
            AddressBech32PrefixDevnet = "kaspadev",
            AddressBech32PrefixSimnet = "kaspasim",
            AddressBech32PrefixTestnet = "kaspatest"
        };
    }

    private static ulong Sompi(decimal amount)
    {
        return (ulong) Math.Floor(amount * KaspaConstants.SmallestUnit);
    }

    [Fact]
    public void BuildSignedTransactionCreatesChangeOutput()
    {
        var coin = CreateCoinTemplate();
        var treasuryKey = KaspaTreasuryKeyDeriver.DeriveFromMnemonic(
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
            KaspaTreasuryKeyDeriver.DefaultDerivationPath,
            KaspaNetwork.Mainnet);

        var payouts = new[]
        {
            new Balance { Address = "kaspa:qpee454h906cyt6pqr5gfegpxx7xjqp79dtwcqz8t698ugulhq8fxg56uaxm9", Amount = 1.25m },
            new Balance { Address = "kaspa:qz382fahc8pv0pn3xnu4d0etkds3764mc7zp8wrsrp3ztt58pu6vclrs67rdl", Amount = 0.5m },
        };

        var utxos = new[]
        {
            new kaspad.UtxosByAddressesEntry
            {
                Address = treasuryKey.Address,
                Outpoint = new kaspad.RpcOutpoint { TransactionId = "4f".PadLeft(64, '0'), Index = 0 },
                UtxoEntry = new kaspad.RpcUtxoEntry
                {
                    Amount = Sompi(4m),
                    ScriptPublicKey = new kaspad.RpcScriptPublicKey
                    {
                        Version = KaspaConstants.PubKeyAddrID,
                        ScriptPublicKey = BuildPayToPubKeyScriptHex(treasuryKey)
                    }
                }
            }
        };

        var builder = new KaspaWalletTransactionBuilder(
            coin,
            "kaspa-mainnet",
            treasuryKey,
            treasuryKey.Address,
            payouts,
            utxos);

        var result = builder.Build();

        Assert.Equal(utxos.Length, result.Transaction.Inputs.Count);
        Assert.Equal(payouts.Length + 1, result.Transaction.Outputs.Count);

        var totalOutputSompi = result.Transaction.Outputs.Sum(x => x.Amount);
        var totalInputSompi = utxos.Sum(x => x.UtxoEntry.Amount);
        Assert.Equal(totalInputSompi - result.Fee, totalOutputSompi);

        Assert.True(result.Transaction.Mass > 0);
        Assert.True(result.Fee > 0);

        var changeOutput = result.Transaction.Outputs.Last();
        var expectedChangeScript = BuildPayToPubKeyScriptBytes(treasuryKey);
        Assert.Equal(Convert.ToHexString(expectedChangeScript).ToLowerInvariant(), changeOutput.ScriptPublicKey.ScriptPublicKey);

        foreach(var input in result.Transaction.Inputs)
        {
            Assert.Equal(132, input.SignatureScript.Length);
            Assert.Equal("01", input.SignatureScript[^2..]);
            Assert.Equal<uint>(1, input.SigOpCount);
        }
    }

    [Fact]
    public void BuildSignedTransactionThrowsWhenFundsInsufficient()
    {
        var coin = CreateCoinTemplate();
        var treasuryKey = KaspaTreasuryKeyDeriver.DeriveFromMnemonic(
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
            KaspaTreasuryKeyDeriver.DefaultDerivationPath,
            KaspaNetwork.Mainnet);

        var payouts = new[]
        {
            new Balance { Address = "kaspa:qpee454h906cyt6pqr5gfegpxx7xjqp79dtwcqz8t698ugulhq8fxg56uaxm9", Amount = 5m }
        };

        var utxos = new[]
        {
            new kaspad.UtxosByAddressesEntry
            {
                Address = treasuryKey.Address,
                Outpoint = new kaspad.RpcOutpoint { TransactionId = "5a".PadLeft(64, '0'), Index = 0 },
                UtxoEntry = new kaspad.RpcUtxoEntry
                {
                    Amount = Sompi(1m),
                    ScriptPublicKey = new kaspad.RpcScriptPublicKey
                    {
                        Version = KaspaConstants.PubKeyAddrID,
                        ScriptPublicKey = BuildPayToPubKeyScriptHex(treasuryKey)
                    }
                }
            }
        };

        var builder = new KaspaWalletTransactionBuilder(
            coin,
            "kaspa-mainnet",
            treasuryKey,
            treasuryKey.Address,
            payouts,
            utxos);

        Assert.Throws<RustyKaspaWalletException>(() => builder.Build());
    }

    private static string BuildPayToPubKeyScriptHex(KaspaDerivedKey treasuryKey)
    {
        var bytes = BuildPayToPubKeyScriptBytes(treasuryKey);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static byte[] BuildPayToPubKeyScriptBytes(KaspaDerivedKey treasuryKey)
    {
        var payload = KaspaUtils.ValidateAddress(treasuryKey.Address, "kaspa-mainnet", CreateCoinTemplate()).Item1.KaspaAddress.ScriptAddress();
        var script = new byte[payload.Length + 2];
        script[0] = 0x20;
        Buffer.BlockCopy(payload, 0, script, 1, payload.Length);
        script[^1] = 0xac;
        return script;
    }
}
