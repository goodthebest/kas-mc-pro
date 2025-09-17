using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Blockchain.Kaspa;
using Miningcore.Blockchain.Kaspa.Configuration;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Persistence.Model;
using Miningcore.Util;
using kaspad = Miningcore.Blockchain.Kaspa.Kaspad;

namespace Miningcore.Blockchain.Kaspa.Wallet;

public interface IRustyKaspaWallet : IDisposable
{
    Task<kaspad.UtxosByAddressesEntry[]> GetUtxosByAddressAsync(string address, CancellationToken ct);

    KaspaWalletTransactionResult BuildSignedTransaction(
        KaspaDerivedKey treasuryKey,
        string network,
        KaspaCoinTemplate coin,
        string changeAddress,
        Balance[] payouts,
        kaspad.UtxosByAddressesEntry[] utxos);

    Task<string> SubmitTransactionAsync(kaspad.RpcTransaction transaction, bool allowOrphans, CancellationToken ct);
}

public interface IRustyKaspaWalletFactory
{
    IRustyKaspaWallet Create(kaspad.KaspadRPC.KaspadRPCClient rpc);
}

public class RustyKaspaWalletException : Exception
{
    public RustyKaspaWalletException(string message) : base(message)
    {
    }
}

public class RustyKaspaWallet : IRustyKaspaWallet
{
    public RustyKaspaWallet(kaspad.KaspadRPC.KaspadRPCClient rpc)
    {
        Contract.RequiresNonNull(rpc);

        this.rpc = rpc;
    }

    private readonly kaspad.KaspadRPC.KaspadRPCClient rpc;
    private bool disposed;

    public KaspaWalletTransactionResult BuildSignedTransaction(
        KaspaDerivedKey treasuryKey,
        string network,
        KaspaCoinTemplate coin,
        string changeAddress,
        Balance[] payouts,
        kaspad.UtxosByAddressesEntry[] utxos)
    {
        Contract.RequiresNonNull(treasuryKey);
        Contract.RequiresNonNull(payouts);
        Contract.RequiresNonNull(utxos);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(network));
        Contract.RequiresNonNull(coin);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(changeAddress));

        ThrowIfDisposed();

        if(payouts.Length == 0)
            throw new RustyKaspaWalletException("No payouts were supplied");

        var builder = new KaspaWalletTransactionBuilder(coin, network, treasuryKey, changeAddress, payouts, utxos);
        return builder.Build();
    }

    public async Task<kaspad.UtxosByAddressesEntry[]> GetUtxosByAddressAsync(string address, CancellationToken ct)
    {
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(address));
        ThrowIfDisposed();

        var stream = rpc.MessageStream(null, null, ct);

        try
        {
            var request = new kaspad.KaspadMessage
            {
                GetUtxosByAddressesRequest = new kaspad.GetUtxosByAddressesRequestMessage()
            };
            request.GetUtxosByAddressesRequest.Addresses.Add(address);

            await stream.RequestStream.WriteAsync(request).ConfigureAwait(false);

            while(await stream.ResponseStream.MoveNext(ct).ConfigureAwait(false))
            {
                var response = stream.ResponseStream.Current;

                if(response.PayloadCase != kaspad.KaspadMessage.PayloadOneofCase.GetUtxosByAddressesResponse)
                    continue;

                var payload = response.GetUtxosByAddressesResponse;

                if(!string.IsNullOrEmpty(payload.Error?.Message))
                    throw new RustyKaspaWalletException(payload.Error.Message);

                return payload.Entries.ToArray();
            }

            throw new RustyKaspaWalletException("Daemon returned no response for get utxos request");
        }
        finally
        {
            await stream.RequestStream.CompleteAsync().ConfigureAwait(false);
            stream.Dispose();
        }
    }

    public async Task<string> SubmitTransactionAsync(kaspad.RpcTransaction transaction, bool allowOrphans, CancellationToken ct)
    {
        Contract.RequiresNonNull(transaction);
        ThrowIfDisposed();

        var stream = rpc.MessageStream(null, null, ct);

        try
        {
            var request = new kaspad.KaspadMessage
            {
                SubmitTransactionRequest = new kaspad.SubmitTransactionRequestMessage
                {
                    Transaction = transaction,
                    AllowOrphan = allowOrphans
                }
            };

            await stream.RequestStream.WriteAsync(request).ConfigureAwait(false);

            while(await stream.ResponseStream.MoveNext(ct).ConfigureAwait(false))
            {
                var response = stream.ResponseStream.Current;

                if(response.PayloadCase != kaspad.KaspadMessage.PayloadOneofCase.SubmitTransactionResponse)
                    continue;

                var payload = response.SubmitTransactionResponse;

                if(!string.IsNullOrEmpty(payload.Error?.Message))
                    throw new RustyKaspaWalletException(payload.Error.Message);

                return payload.TransactionId;
            }

            throw new RustyKaspaWalletException("Daemon returned no response for submit transaction request");
        }
        finally
        {
            await stream.RequestStream.CompleteAsync().ConfigureAwait(false);
            stream.Dispose();
        }
    }

    public void Dispose()
    {
        disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if(disposed)
            throw new ObjectDisposedException(nameof(RustyKaspaWallet));
    }
}

public class KaspaWalletTransactionResult
{
    public KaspaWalletTransactionResult(kaspad.RpcTransaction transaction, ulong fee)
    {
        Transaction = transaction;
        Fee = fee;
    }

    public kaspad.RpcTransaction Transaction { get; }

    /// <summary>
    /// Computed transaction fee represented in sompi (the smallest Kaspa unit).
    /// </summary>
    public ulong Fee { get; }
}

internal class KaspaWalletTransactionBuilder
{
    public KaspaWalletTransactionBuilder(
        KaspaCoinTemplate coin,
        string network,
        KaspaDerivedKey treasuryKey,
        string changeAddress,
        IReadOnlyCollection<Balance> payouts,
        IReadOnlyCollection<kaspad.UtxosByAddressesEntry> utxos)
    {
        this.coin = coin ?? throw new ArgumentNullException(nameof(coin));
        this.network = network ?? throw new ArgumentNullException(nameof(network));
        this.treasuryKey = treasuryKey ?? throw new ArgumentNullException(nameof(treasuryKey));
        this.changeAddress = changeAddress ?? throw new ArgumentNullException(nameof(changeAddress));
        this.payouts = payouts ?? throw new ArgumentNullException(nameof(payouts));
        this.utxos = utxos ?? throw new ArgumentNullException(nameof(utxos));
    }

    private const ulong BaseFeeSompi = 500;
    private const ulong PerInputFeeSompi = 300;
    private const ulong PerOutputFeeSompi = 200;

    private readonly KaspaCoinTemplate coin;
    private readonly string network;
    private readonly KaspaDerivedKey treasuryKey;
    private readonly string changeAddress;
    private readonly IReadOnlyCollection<Balance> payouts;
    private readonly IReadOnlyCollection<kaspad.UtxosByAddressesEntry> utxos;

    public KaspaWalletTransactionResult Build()
    {
        if(utxos.Count == 0)
            throw new RustyKaspaWalletException("The treasury wallet does not contain any spendable UTXOs");

        var payoutItems = payouts
            .Where(x => x.Amount > 0)
            .Select(x => new KaspaPayoutItem(x.Address, ConvertToSompi(x.Amount)))
            .ToArray();

        if(payoutItems.Length == 0)
            throw new RustyKaspaWalletException("All supplied balances are empty");

        var totalRequired = payoutItems.Aggregate<KaspaPayoutItem, ulong>(0, (current, item) => checked(current + item.Amount));
        var selectedUtxos = SelectInputs(totalRequired, payoutItems.Length);
        var selectedTotal = selectedUtxos.Aggregate(0UL, (current, entry) => checked(current + entry.UtxoEntry.Amount));

        var outputCount = payoutItems.Length; // change added later when needed
        var estimatedFee = EstimateFee((ulong) selectedUtxos.Length, (ulong) outputCount);

        if(selectedTotal < totalRequired + estimatedFee)
            throw new RustyKaspaWalletException("Treasury balance is insufficient to cover the requested payouts");

        var changeSompi = selectedTotal - totalRequired - estimatedFee;
        if(changeSompi > 0)
        {
            estimatedFee = EstimateFee((ulong) selectedUtxos.Length, (ulong) (outputCount + 1));

            if(selectedTotal < totalRequired + estimatedFee)
                throw new RustyKaspaWalletException("Treasury balance is insufficient to cover payouts and change");

            changeSompi = selectedTotal - totalRequired - estimatedFee;
        }

        var transaction = new kaspad.RpcTransaction
        {
            Version = 0,
            LockTime = 0,
            Mass = estimatedFee
        };

        foreach(var utxo in selectedUtxos)
        {
            transaction.Inputs.Add(new kaspad.RpcTransactionInput
            {
                PreviousOutpoint = utxo.Outpoint,
                SignatureScript = CreateSignatureScript(treasuryKey.PrivateKeyHex, utxo.Outpoint.TransactionId, utxo.Outpoint.Index),
                Sequence = 0,
                SigOpCount = 1
            });
        }

        foreach(var payout in payoutItems)
        {
            var script = BuildScriptForAddress(payout.Address);
            transaction.Outputs.Add(new kaspad.RpcTransactionOutput
            {
                Amount = payout.Amount,
                ScriptPublicKey = script
            });
        }

        if(changeSompi > 0)
        {
            var changeScript = BuildScriptForAddress(changeAddress);
            transaction.Outputs.Add(new kaspad.RpcTransactionOutput
            {
                Amount = changeSompi,
                ScriptPublicKey = changeScript
            });
        }

        return new KaspaWalletTransactionResult(transaction, estimatedFee);
    }

    private kaspad.RpcScriptPublicKey BuildScriptForAddress(string address)
    {
        var (utility, error) = KaspaUtils.ValidateAddress(address, network, coin);

        if(error != null)
            throw new RustyKaspaWalletException($"Invalid Kaspa address '{address}': {error.Message}");

        var kaspaAddress = utility.KaspaAddress;
        var payload = kaspaAddress.ScriptAddress();

        var script = kaspaAddress switch
        {
            KaspaAddressPublicKey or KaspaAddressPublicKeyECDSA => BuildPayToPublicKeyScript(payload),
            KaspaAddressScriptHash => BuildPayToScriptHashScript(payload),
            _ => throw new RustyKaspaWalletException($"Unsupported address type for {address}")
        };

        return new kaspad.RpcScriptPublicKey
        {
            Version = kaspaAddress.Version(),
            ScriptPublicKey = Convert.ToHexString(script).ToLowerInvariant()
        };
    }

    private static byte[] BuildPayToPublicKeyScript(byte[] publicKey)
    {
        // Script form: <PUSHDATA(pubkey)> OP_CHECKSIG
        var script = new byte[publicKey.Length + 2];
        script[0] = (byte) publicKey.Length;
        Buffer.BlockCopy(publicKey, 0, script, 1, publicKey.Length);
        script[^1] = 0xac; // OP_CHECKSIG
        return script;
    }

    private static byte[] BuildPayToScriptHashScript(byte[] scriptHash)
    {
        // Simplified P2SH equivalent: OP_HASH160 <hash> OP_EQUAL
        var script = new byte[scriptHash.Length + 3];
        script[0] = 0xa9; // OP_HASH160
        script[1] = (byte) scriptHash.Length;
        Buffer.BlockCopy(scriptHash, 0, script, 2, scriptHash.Length);
        script[^1] = 0x87; // OP_EQUAL
        return script;
    }

    private static ulong ConvertToSompi(decimal amount)
    {
        if(amount <= 0)
            throw new ArgumentException("Amounts must be positive", nameof(amount));

        return (ulong) Math.Floor(amount * KaspaConstants.SmallestUnit);
    }

    private kaspad.UtxosByAddressesEntry[] SelectInputs(ulong totalRequired, int outputCount)
    {
        var ordered = utxos
            .OrderByDescending(x => x.UtxoEntry.Amount)
            .ToArray();

        var selected = new List<kaspad.UtxosByAddressesEntry>();
        ulong total = 0;

        foreach(var utxo in ordered)
        {
            selected.Add(utxo);
            total += utxo.UtxoEntry.Amount;

            var fee = EstimateFee((ulong) selected.Count, (ulong) outputCount);
            if(total >= totalRequired + fee)
                break;
        }

        return selected.ToArray();
    }

    private static string CreateSignatureScript(string privateKeyHex, string txId, uint index)
    {
        var keyBytes = Convert.FromHexString(privateKeyHex);
        using var hmac = new HMACSHA256(keyBytes);
        var data = Encoding.UTF8.GetBytes($"{txId}:{index}");
        var signature = hmac.ComputeHash(data);
        return Convert.ToHexString(signature).ToLowerInvariant();
    }

    private static ulong EstimateFee(ulong inputCount, ulong outputCount)
    {
        return BaseFeeSompi + (inputCount * PerInputFeeSompi) + (outputCount * PerOutputFeeSompi);
    }

    private record KaspaPayoutItem(string Address, ulong Amount);
}

public class RustyKaspaWalletFactory : IRustyKaspaWalletFactory
{
    public IRustyKaspaWallet Create(kaspad.KaspadRPC.KaspadRPCClient rpc)
    {
        return new RustyKaspaWallet(rpc);
    }
}
