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
            new kaspad.GetUtxosByAddressesResponseMessage.Types.Entry
            {
                Address = treasuryKey.Address,
                Outpoint = new kaspad.RpcOutpoint { TransactionId = "abc", Index = 0 },
                UtxoEntry = new kaspad.RpcUtxoEntry { Amount = Sompi(4m) }
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
        Assert.Equal(payouts.Length + 1, result.Transaction.Outputs.Count); // payouts + change

        var totalOutputSompi = result.Transaction.Outputs.Sum(x => x.Amount);
        Assert.Equal(utxos.Sum(x => x.UtxoEntry.Amount) - result.Fee, totalOutputSompi);

        var expectedSignature = ComputeSignature(treasuryKey.PrivateKeyHex, utxos[0].Outpoint.TransactionId, utxos[0].Outpoint.Index);
        Assert.Equal(expectedSignature, result.Transaction.Inputs[0].SignatureScript);
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
            new kaspad.GetUtxosByAddressesResponseMessage.Types.Entry
            {
                Address = treasuryKey.Address,
                Outpoint = new kaspad.RpcOutpoint { TransactionId = "abc", Index = 0 },
                UtxoEntry = new kaspad.RpcUtxoEntry { Amount = Sompi(1m) }
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

    private static string ComputeSignature(string privateKeyHex, string txId, uint index)
    {
        var keyBytes = Convert.FromHexString(privateKeyHex);
        using var hmac = new System.Security.Cryptography.HMACSHA256(keyBytes);
        var data = System.Text.Encoding.UTF8.GetBytes($"{txId}:{index}");
        return Convert.ToHexString(hmac.ComputeHash(data)).ToLowerInvariant();
    }
}
