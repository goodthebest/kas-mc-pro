using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.Util;
using NBitcoin.Secp256k1;
using kaspad = Miningcore.Blockchain.Kaspa.Kaspad;

namespace Miningcore.Blockchain.Kaspa.Wallet;

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

        var payoutItems = BuildPayoutItems();

        if(payoutItems.Count == 0)
            throw new RustyKaspaWalletException("All supplied balances are empty");

        var preparedUtxos = PrepareUtxos();

        if(preparedUtxos.Count == 0)
            throw new RustyKaspaWalletException("The treasury wallet does not contain any spendable UTXOs");

        var payoutTotal = payoutItems.Aggregate(0UL, (current, item) => checked(current + item.Amount));
        var sortedUtxos = preparedUtxos
            .OrderByDescending(x => x.Entry.Amount)
            .ToArray();

        var selected = new List<KaspaWalletUtxo>();
        ulong selectedTotal = 0;
        var utxoIndex = 0;
        var massCalculator = new KaspaTransactionMassCalculator();
        var changeScript = BuildScriptForAddress(changeAddress);

        while(true)
        {
            if(selectedTotal < payoutTotal)
            {
                if(utxoIndex >= sortedUtxos.Length)
                    throw new RustyKaspaWalletException("Treasury balance is insufficient to cover the requested payouts");

                var next = sortedUtxos[utxoIndex++];
                selected.Add(next);
                selectedTotal = checked(selectedTotal + next.Entry.Amount);
                continue;
            }

            var attempt = TryBuildTransaction(selected, payoutItems, selectedTotal, payoutTotal, massCalculator, changeScript);

            switch(attempt.Status)
            {
                case KaspaTransactionBuildStatus.Success:
                    return attempt.Result!;

                case KaspaTransactionBuildStatus.NeedsMoreFunds:
                    if(utxoIndex >= sortedUtxos.Length)
                        throw new RustyKaspaWalletException("Treasury balance is insufficient to cover the requested payouts");

                    var additional = sortedUtxos[utxoIndex++];
                    selected.Add(additional);
                    selectedTotal = checked(selectedTotal + additional.Entry.Amount);
                    continue;

                case KaspaTransactionBuildStatus.Failure:
                    throw new RustyKaspaWalletException(attempt.Error ?? "Unable to assemble Kaspa transaction");

                default:
                    throw new InvalidOperationException("Unexpected payout build result");
            }
        }
    }

    private KaspaWalletTransactionAttempt TryBuildTransaction(
        IReadOnlyList<KaspaWalletUtxo> selected,
        IReadOnlyList<KaspaPayoutItem> payoutItems,
        ulong selectedTotal,
        ulong payoutTotal,
        KaspaTransactionMassCalculator massCalculator,
        KaspaScriptPublicKey changeScript)
    {
        if(selected.Count == 0)
            return KaspaWalletTransactionAttempt.NeedsFunds();

        var utxoEntries = selected.Select(x => x.Entry).ToArray();
        var includeChange = false;
        ulong changeAmount = 0;
        KaspaTransactionMassResult massInfo = default;
        KaspaTransaction tx;
        IReadOnlyList<KaspaTransactionOutput> outputs = Array.Empty<KaspaTransactionOutput>();
        const int maxIterations = 8;

        for(var iteration = 0; iteration < maxIterations; iteration++)
        {
            outputs = CreateOutputs(payoutItems, includeChange ? changeAmount : 0, includeChange ? changeScript : null);
            tx = KaspaTransaction.CreateUnsigned(selected, outputs);

            massInfo = massCalculator.CalculateUnsigned(tx, utxoEntries);
            var requiredFee = Math.Max(massInfo.OverallMass, KaspaConstants.MinimumRelayTransactionFee);
            var totalRequired = checked(payoutTotal + requiredFee);

            if(selectedTotal < totalRequired)
                return KaspaWalletTransactionAttempt.NeedsFunds();

            var potentialChange = selectedTotal - totalRequired;

            if(includeChange)
            {
                if(potentialChange == 0)
                {
                    includeChange = false;
                    changeAmount = 0;
                    continue;
                }

                var changeOutput = new KaspaTransactionOutput(potentialChange, changeScript);

                if(massCalculator.IsDust(changeOutput))
                {
                    includeChange = false;
                    changeAmount = 0;
                    continue;
                }

                if(potentialChange == changeAmount)
                    return FinalizeTransaction(tx, selected, outputs, utxoEntries, massInfo, selectedTotal);

                changeAmount = potentialChange;
                continue;
            }

            if(potentialChange > 0)
            {
                var changeOutput = new KaspaTransactionOutput(potentialChange, changeScript);

                if(!massCalculator.IsDust(changeOutput))
                {
                    includeChange = true;
                    changeAmount = potentialChange;
                    continue;
                }
            }

            return FinalizeTransaction(tx, selected, outputs, utxoEntries, massInfo, selectedTotal);
        }

        return KaspaWalletTransactionAttempt.Failure("Unable to stabilize change output during transaction assembly");
    }

    private KaspaWalletTransactionAttempt FinalizeTransaction(
        KaspaTransaction transaction,
        IReadOnlyList<KaspaWalletUtxo> selected,
        IReadOnlyList<KaspaTransactionOutput> outputs,
        IReadOnlyList<KaspaUtxoEntry> utxoEntries,
        KaspaTransactionMassResult massInfo,
        ulong selectedTotal)
    {
        KaspaTransactionSigner.Sign(transaction, treasuryKey, utxoEntries);
        transaction.Mass = massInfo.OverallMass;

        var rpcTransaction = transaction.ToRpcTransaction();
        var outputTotal = outputs.Aggregate(0UL, (current, output) => checked(current + output.Amount));
        var fee = selectedTotal - outputTotal;

        return KaspaWalletTransactionAttempt.Success(new KaspaWalletTransactionResult(rpcTransaction, fee));
    }

    private IReadOnlyList<KaspaPayoutItem> BuildPayoutItems()
    {
        var result = new List<KaspaPayoutItem>();

        foreach(var payout in payouts)
        {
            if(payout.Amount <= 0)
                continue;

            var amount = ConvertToSompi(payout.Amount);
            var script = BuildScriptForAddress(payout.Address);
            result.Add(new KaspaPayoutItem(payout.Address, amount, script));
        }

        return result;
    }

    private IReadOnlyList<KaspaWalletUtxo> PrepareUtxos()
    {
        var result = new List<KaspaWalletUtxo>();

        foreach(var entry in utxos)
        {
            if(entry?.UtxoEntry == null || entry.Outpoint == null)
                continue;

            if(entry.UtxoEntry.Amount == 0)
                continue;

            var script = KaspaScriptPublicKey.FromRpc(entry.UtxoEntry.ScriptPublicKey);
            var utxoEntry = new KaspaUtxoEntry(entry.UtxoEntry.Amount, script, entry.UtxoEntry.IsCoinbase, entry.UtxoEntry.BlockDaaScore);
            result.Add(new KaspaWalletUtxo(entry.Outpoint, utxoEntry));
        }

        return result;
    }

    private KaspaScriptPublicKey BuildScriptForAddress(string address)
    {
        var (utility, error) = KaspaUtils.ValidateAddress(address, network, coin);

        if(error != null)
            throw new RustyKaspaWalletException($"Invalid Kaspa address '{address}': {error.Message}");

        var kaspaAddress = utility.KaspaAddress;
        var payload = kaspaAddress.ScriptAddress();

        byte[] script = kaspaAddress switch
        {
            KaspaAddressPublicKey => BuildPayToSchnorrScript(payload),
            KaspaAddressPublicKeyECDSA => BuildPayToEcdsaScript(payload),
            KaspaAddressScriptHash => BuildPayToScriptHashScript(payload),
            _ => throw new RustyKaspaWalletException($"Unsupported address type for {address}")
        };

        return new KaspaScriptPublicKey(kaspaAddress.Version(), script);
    }

    private static byte[] BuildPayToSchnorrScript(byte[] publicKey)
    {
        var script = new byte[publicKey.Length + 2];
        script[0] = 0x20;
        Buffer.BlockCopy(publicKey, 0, script, 1, publicKey.Length);
        script[^1] = 0xac;
        return script;
    }

    private static byte[] BuildPayToEcdsaScript(byte[] publicKey)
    {
        var script = new byte[publicKey.Length + 2];
        script[0] = 0x21;
        Buffer.BlockCopy(publicKey, 0, script, 1, publicKey.Length);
        script[^1] = 0xab;
        return script;
    }

    private static byte[] BuildPayToScriptHashScript(byte[] scriptHash)
    {
        var script = new byte[scriptHash.Length + 3];
        script[0] = 0xaa;
        script[1] = 0x20;
        Buffer.BlockCopy(scriptHash, 0, script, 2, scriptHash.Length);
        script[^1] = 0x87;
        return script;
    }

    private static IReadOnlyList<KaspaTransactionOutput> CreateOutputs(
        IReadOnlyList<KaspaPayoutItem> payouts,
        ulong changeAmount,
        KaspaScriptPublicKey? changeScript)
    {
        var result = new List<KaspaTransactionOutput>(payouts.Count + (changeAmount > 0 && changeScript != null ? 1 : 0));

        foreach(var payout in payouts)
            result.Add(new KaspaTransactionOutput(payout.Amount, payout.Script));

        if(changeAmount > 0 && changeScript != null)
            result.Add(new KaspaTransactionOutput(changeAmount, changeScript));

        return result;
    }

    private static ulong ConvertToSompi(decimal amount)
    {
        if(amount <= 0)
            throw new ArgumentException("Amounts must be positive", nameof(amount));

        var sompi = decimal.Floor(amount * KaspaConstants.SmallestUnit);

        if(sompi > ulong.MaxValue)
            throw new RustyKaspaWalletException("Payout amount exceeds maximum Kaspa value");

        return (ulong) sompi;
    }

    private sealed record KaspaPayoutItem(string Address, ulong Amount, KaspaScriptPublicKey Script);
}

internal enum KaspaTransactionBuildStatus
{
    Success,
    NeedsMoreFunds,
    Failure
}

internal readonly struct KaspaWalletTransactionAttempt
{
    private KaspaWalletTransactionAttempt(KaspaTransactionBuildStatus status, KaspaWalletTransactionResult? result, string? error)
    {
        Status = status;
        Result = result;
        Error = error;
    }

    public KaspaTransactionBuildStatus Status { get; }
    public KaspaWalletTransactionResult? Result { get; }
    public string? Error { get; }

    public static KaspaWalletTransactionAttempt Success(KaspaWalletTransactionResult result) =>
        new(KaspaTransactionBuildStatus.Success, result, null);

    public static KaspaWalletTransactionAttempt NeedsFunds() =>
        new(KaspaTransactionBuildStatus.NeedsMoreFunds, null, null);

    public static KaspaWalletTransactionAttempt Failure(string error) =>
        new(KaspaTransactionBuildStatus.Failure, null, error);
}

internal sealed class KaspaTransaction
{
    private KaspaTransaction()
    {
        Inputs = new List<KaspaTransactionInput>();
        Outputs = new List<KaspaTransactionOutput>();
        Payload = Array.Empty<byte>();
        SubnetworkId = new byte[KaspaConstants.SubnetworkIdLength];
    }

    public ushort Version { get; set; }
    public ulong LockTime { get; set; }
    public ulong Gas { get; set; }
    public List<KaspaTransactionInput> Inputs { get; }
    public List<KaspaTransactionOutput> Outputs { get; }
    public byte[] Payload { get; set; }
    public byte[] SubnetworkId { get; set; }
    public ulong Mass { get; set; }

    public static KaspaTransaction CreateUnsigned(IReadOnlyList<KaspaWalletUtxo> utxos, IReadOnlyList<KaspaTransactionOutput> outputs)
    {
        var tx = new KaspaTransaction
        {
            Version = 0,
            LockTime = 0,
            Gas = 0
        };

        foreach(var utxo in utxos)
            tx.Inputs.Add(new KaspaTransactionInput(utxo));

        foreach(var output in outputs)
            tx.Outputs.Add(output.Clone());

        return tx;
    }

    public kaspad.RpcTransaction ToRpcTransaction()
    {
        var rpc = new kaspad.RpcTransaction
        {
            Version = Version,
            LockTime = LockTime,
            Gas = Gas,
            Mass = Mass
        };

        rpc.SubnetworkId = SubnetworkId.Any(b => b != 0)
            ? Convert.ToHexString(SubnetworkId).ToLowerInvariant()
            : string.Empty;

        rpc.Payload = Payload.Length > 0
            ? Convert.ToHexString(Payload).ToLowerInvariant()
            : string.Empty;

        foreach(var input in Inputs)
            rpc.Inputs.Add(input.ToRpc());

        foreach(var output in Outputs)
            rpc.Outputs.Add(output.ToRpc());

        return rpc;
    }
}

internal sealed class KaspaTransactionInput
{
    public KaspaTransactionInput(KaspaWalletUtxo utxo)
    {
        TransactionIdHex = utxo.TransactionIdHex;
        TransactionIdBytes = utxo.TransactionIdBytes;
        Index = utxo.Index;
        SignatureScript = Array.Empty<byte>();
        Sequence = 0;
        SigOpCount = 1;
    }

    public string TransactionIdHex { get; }
    public byte[] TransactionIdBytes { get; }
    public uint Index { get; }
    public byte[] SignatureScript { get; set; }
    public ulong Sequence { get; set; }
    public uint SigOpCount { get; set; }

    public kaspad.RpcTransactionInput ToRpc()
    {
        return new kaspad.RpcTransactionInput
        {
            PreviousOutpoint = new kaspad.RpcOutpoint
            {
                TransactionId = TransactionIdHex,
                Index = Index
            },
            SignatureScript = Convert.ToHexString(SignatureScript).ToLowerInvariant(),
            Sequence = Sequence,
            SigOpCount = SigOpCount
        };
    }
}

internal sealed class KaspaTransactionOutput
{
    public KaspaTransactionOutput(ulong amount, KaspaScriptPublicKey scriptPublicKey)
    {
        Amount = amount;
        ScriptPublicKey = scriptPublicKey ?? throw new ArgumentNullException(nameof(scriptPublicKey));
    }

    public ulong Amount { get; }
    public KaspaScriptPublicKey ScriptPublicKey { get; }

    public KaspaTransactionOutput Clone() => new(Amount, ScriptPublicKey);

    public kaspad.RpcTransactionOutput ToRpc()
    {
        return new kaspad.RpcTransactionOutput
        {
            Amount = Amount,
            ScriptPublicKey = ScriptPublicKey.ToRpc()
        };
    }
}

internal sealed class KaspaScriptPublicKey
{
    public KaspaScriptPublicKey(ushort version, byte[] script)
    {
        Version = version;
        Script = script ?? throw new ArgumentNullException(nameof(script));
    }

    public ushort Version { get; }
    public byte[] Script { get; }

    public kaspad.RpcScriptPublicKey ToRpc()
    {
        return new kaspad.RpcScriptPublicKey
        {
            Version = Version,
            ScriptPublicKey = Convert.ToHexString(Script).ToLowerInvariant()
        };
    }

    public static KaspaScriptPublicKey FromRpc(kaspad.RpcScriptPublicKey script)
    {
        if(script == null)
            throw new ArgumentNullException(nameof(script));

        var bytes = string.IsNullOrEmpty(script.ScriptPublicKey)
            ? Array.Empty<byte>()
            : script.ScriptPublicKey.HexToByteArray();

        return new KaspaScriptPublicKey(checked((ushort) script.Version), bytes);
    }
}

internal sealed class KaspaUtxoEntry
{
    public KaspaUtxoEntry(ulong amount, KaspaScriptPublicKey script, bool isCoinbase, ulong blockDaaScore)
    {
        Amount = amount;
        Script = script ?? throw new ArgumentNullException(nameof(script));
        IsCoinbase = isCoinbase;
        BlockDaaScore = blockDaaScore;
    }

    public ulong Amount { get; }
    public KaspaScriptPublicKey Script { get; }
    public bool IsCoinbase { get; }
    public ulong BlockDaaScore { get; }
}

internal sealed class KaspaWalletUtxo
{
    public KaspaWalletUtxo(kaspad.RpcOutpoint outpoint, KaspaUtxoEntry entry)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        TransactionIdHex = outpoint.TransactionId;
        TransactionIdBytes = string.IsNullOrEmpty(outpoint.TransactionId)
            ? throw new RustyKaspaWalletException("UTXO outpoint is missing transaction id")
            : outpoint.TransactionId.HexToByteArray();
        Index = outpoint.Index;
    }

    public KaspaUtxoEntry Entry { get; }
    public string TransactionIdHex { get; }
    public byte[] TransactionIdBytes { get; }
    public uint Index { get; }
}

internal sealed class KaspaTransactionMassCalculator
{
    private const ulong MassPerTxByte = 1;
    private const ulong MassPerScriptPublicKeyByte = 10;
    private const ulong MassPerSigOp = 1000;
    private const int SignatureSize = 66;
    private const int OutpointSize = 32 + 4;

    public KaspaTransactionMassResult CalculateUnsigned(KaspaTransaction tx, IReadOnlyList<KaspaUtxoEntry> inputs)
    {
        var baseMass = BlankTransactionMass()
            + PayloadMass(tx)
            + OutputsMass(tx.Outputs)
            + InputsMass(tx.Inputs);

        var signatureMass = (ulong) tx.Inputs.Count * SignatureSize * MassPerTxByte;
        var storageMass = CalculateStorageMass(tx.Outputs, inputs);

        return new KaspaTransactionMassResult(baseMass, signatureMass, storageMass);
    }

    public bool IsDust(KaspaTransactionOutput output)
    {
        var serializedSize = TransactionOutputSerializedByteSize(output) + 148UL;
        var numerator = (decimal) output.Amount * 1000m;
        var denominator = 3m * serializedSize;
        var ratio = numerator / denominator;
        return ratio < KaspaConstants.MinimumRelayTransactionFee;
    }

    private static ulong BlankTransactionMass()
    {
        var size = 2UL + 8UL + 8UL + 8UL + (ulong) KaspaConstants.SubnetworkIdLength + 8UL + 32UL + 8UL;
        return size * MassPerTxByte;
    }

    private static ulong PayloadMass(KaspaTransaction tx) => (ulong) tx.Payload.Length * MassPerTxByte;

    private static ulong OutputsMass(IEnumerable<KaspaTransactionOutput> outputs)
    {
        ulong total = 0;

        foreach(var output in outputs)
        {
            var scriptLength = (ulong) output.ScriptPublicKey.Script.Length;
            total += MassPerScriptPublicKeyByte * (2UL + scriptLength);
            total += TransactionOutputSerializedByteSize(output) * MassPerTxByte;
        }

        return total;
    }

    private static ulong InputsMass(IEnumerable<KaspaTransactionInput> inputs)
    {
        ulong total = 0;

        foreach(var input in inputs)
        {
            total += MassPerSigOp * input.SigOpCount;
            total += TransactionInputSerializedByteSize(input) * MassPerTxByte;
        }

        return total;
    }

    private static ulong TransactionInputSerializedByteSize(KaspaTransactionInput input)
    {
        var size = (ulong) OutpointSize;
        size += 8UL;
        size += (ulong) input.SignatureScript.Length;
        size += 8UL;
        return size;
    }

    private static ulong TransactionOutputSerializedByteSize(KaspaTransactionOutput output)
    {
        var size = 8UL;
        size += 2UL;
        size += 8UL;
        size += (ulong) output.ScriptPublicKey.Script.Length;
        return size;
    }

    private static ulong CalculateStorageMass(IReadOnlyList<KaspaTransactionOutput> outputs, IReadOnlyList<KaspaUtxoEntry> inputs)
    {
        if(inputs.Count == 0)
            throw new RustyKaspaWalletException("Treasury wallet does not contain any spendable UTXOs");

        var outputCells = outputs.Select(x => new UtxoCell(CalculatePlurality(x.ScriptPublicKey), x.Amount)).ToArray();
        var inputCells = inputs.Select(x => new UtxoCell(CalculatePlurality(x.Script), x.Amount)).ToArray();

        return CalcStorageMass(false, inputCells, outputCells, KaspaConstants.StorageMassParameter);
    }

    private static ulong CalculatePlurality(KaspaScriptPublicKey script)
    {
        const int utxoConstStorage = 32 + 4 + 8 + 8 + 1 + 2 + 8;
        const int utxoUnitSize = 100;
        var length = script.Script.Length;
        var total = utxoConstStorage + length;
        return (ulong) ((total + utxoUnitSize - 1) / utxoUnitSize);
    }

    private static ulong CalcStorageMass(bool isCoinbase, IReadOnlyList<UtxoCell> inputs, IReadOnlyList<UtxoCell> outputs, ulong stormParam)
    {
        if(isCoinbase)
            return 0;

        ulong outsPlurality = 0;
        ulong harmonicOuts = 0;

        foreach(var output in outputs)
        {
            if(output.Amount == 0)
                throw new RustyKaspaWalletException("Encountered zero-valued output when computing transaction mass");

            outsPlurality = checked(outsPlurality + output.Plurality);
            harmonicOuts = checked(harmonicOuts + checked(stormParam * output.Plurality * output.Plurality) / output.Amount);
        }

        bool relaxed;

        if(outsPlurality == 1)
            relaxed = true;
        else if(inputs.Count > 2)
            relaxed = false;
        else
        {
            var insPlurality = inputs.Aggregate(0UL, (current, cell) => checked(current + cell.Plurality));
            relaxed = insPlurality == 1 || (outsPlurality == 2 && insPlurality == 2);
        }

        if(relaxed)
        {
            ulong harmonicIns = 0;

            foreach(var input in inputs)
            {
                if(input.Amount == 0)
                    throw new RustyKaspaWalletException("Encountered zero-valued input when computing transaction mass");

                harmonicIns = checked(harmonicIns + checked(stormParam * input.Plurality * input.Plurality) / input.Amount);
            }

            return harmonicOuts > harmonicIns ? harmonicOuts - harmonicIns : 0UL;
        }

        var totalPlurality = inputs.Aggregate(0UL, (current, cell) => checked(current + cell.Plurality));
        var sumAmounts = inputs.Aggregate(0UL, (current, cell) => checked(current + cell.Amount));

        if(totalPlurality == 0)
            throw new RustyKaspaWalletException("Unable to compute storage mass for empty input set");

        var meanIns = sumAmounts / totalPlurality;
        if(meanIns == 0)
            throw new RustyKaspaWalletException("Unable to compute storage mass for zero-valued inputs");

        var arithmeticIns = checked(totalPlurality * (stormParam / meanIns));
        return harmonicOuts > arithmeticIns ? harmonicOuts - arithmeticIns : 0UL;
    }

    private readonly record struct UtxoCell(ulong Plurality, ulong Amount);
}

internal readonly struct KaspaTransactionMassResult
{
    public KaspaTransactionMassResult(ulong baseMass, ulong signatureMass, ulong storageMass)
    {
        BaseMass = baseMass;
        SignatureMass = signatureMass;
        StorageMass = storageMass;
    }

    public ulong BaseMass { get; }
    public ulong SignatureMass { get; }
    public ulong StorageMass { get; }
    public ulong UnsignedComputeMass => BaseMass + SignatureMass;
    public ulong OverallMass => Math.Max(UnsignedComputeMass, StorageMass);
}

internal static class KaspaTransactionSigner
{
    public static void Sign(KaspaTransaction transaction, KaspaDerivedKey treasuryKey, IReadOnlyList<KaspaUtxoEntry> utxoEntries)
    {
        var privateKeyBytes = Convert.FromHexString(treasuryKey.PrivateKeyHex);
        var privateKey = ECPrivKey.Create(privateKeyBytes);

        if(transaction.Inputs.Count != utxoEntries.Count)
            throw new RustyKaspaWalletException("Mismatch between transaction inputs and UTXO entries");

        for(var i = 0; i < transaction.Inputs.Count; i++)
        {
            var hash = KaspaSigHash.ComputeSignatureHash(transaction, utxoEntries, i);
            var signature = privateKey.SignBIP340(hash);
            Span<byte> signatureBytes = stackalloc byte[64];
            signature.WriteToSpan(signatureBytes);

            var script = new byte[66];
            script[0] = 65;
            signatureBytes.CopyTo(script.AsSpan(1));
            script[^1] = 0x01;

            transaction.Inputs[i].SignatureScript = script;
            transaction.Inputs[i].SigOpCount = 1;
        }
    }
}

internal static class KaspaSigHash
{
    private static readonly byte[] TransactionSigningHashKey = Encoding.ASCII.GetBytes("TransactionSigningHash");
    private static readonly byte[] ZeroHash = new byte[32];

    public static byte[] ComputeSignatureHash(KaspaTransaction transaction, IReadOnlyList<KaspaUtxoEntry> utxoEntries, int inputIndex)
    {
        var input = transaction.Inputs[inputIndex];
        var tx = transaction;

        var prevoutsHash = ComputeHash(writer =>
        {
            foreach(var txInput in tx.Inputs)
            {
                writer.WriteBytes(txInput.TransactionIdBytes);
                writer.WriteUInt32(txInput.Index);
            }
        });

        var sequencesHash = ComputeHash(writer =>
        {
            foreach(var txInput in tx.Inputs)
                writer.WriteUInt64(txInput.Sequence);
        });

        var sigOpCountsHash = ComputeHash(writer =>
        {
            foreach(var txInput in tx.Inputs)
                writer.WriteByte((byte) txInput.SigOpCount);
        });

        var outputsHash = ComputeHash(writer =>
        {
            foreach(var output in tx.Outputs)
            {
                writer.WriteUInt64(output.Amount);
                writer.WriteUInt16(output.ScriptPublicKey.Version);
                writer.WriteVarBytes(output.ScriptPublicKey.Script);
            }
        });

        var payloadHash = tx.Payload.Length == 0 && tx.SubnetworkId.All(b => b == 0)
            ? ZeroHash
            : ComputeHash(writer =>
            {
                writer.WriteVarBytes(tx.Payload);
            });

        var utxo = utxoEntries[inputIndex];

        return ComputeHash(writer =>
        {
            writer.WriteUInt16(tx.Version);
            writer.WriteBytes(prevoutsHash);
            writer.WriteBytes(sequencesHash);
            writer.WriteBytes(sigOpCountsHash);
            writer.WriteBytes(input.TransactionIdBytes);
            writer.WriteUInt32(input.Index);
            writer.WriteUInt16(utxo.Script.Version);
            writer.WriteVarBytes(utxo.Script.Script);
            writer.WriteUInt64(utxo.Amount);
            writer.WriteUInt64(input.Sequence);
            writer.WriteByte((byte) input.SigOpCount);
            writer.WriteBytes(outputsHash);
            writer.WriteUInt64(tx.LockTime);
            writer.WriteBytes(tx.SubnetworkId);
            writer.WriteUInt64(tx.Gas);
            writer.WriteBytes(payloadHash);
            writer.WriteByte(0x01);
        });
    }

    private static byte[] ComputeHash(Action<HashWriter> build)
    {
        var writer = new HashWriter();
        build(writer);
        Span<byte> result = stackalloc byte[32];
        var hasher = new Blake2b(TransactionSigningHashKey);
        hasher.Digest(writer.WrittenSpan, result);
        return result.ToArray();
    }
}

internal sealed class HashWriter
{
    private readonly ArrayBufferWriter<byte> buffer = new();

    public ReadOnlySpan<byte> WrittenSpan => buffer.WrittenSpan;

    public void WriteByte(byte value)
    {
        var span = buffer.GetSpan(1);
        span[0] = value;
        buffer.Advance(1);
    }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        var span = buffer.GetSpan(value.Length);
        value.CopyTo(span);
        buffer.Advance(value.Length);
    }

    public void WriteUInt16(ushort value)
    {
        var span = buffer.GetSpan(2);
        BinaryPrimitives.WriteUInt16LittleEndian(span, value);
        buffer.Advance(2);
    }

    public void WriteUInt32(uint value)
    {
        var span = buffer.GetSpan(4);
        BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        buffer.Advance(4);
    }

    public void WriteUInt64(ulong value)
    {
        var span = buffer.GetSpan(8);
        BinaryPrimitives.WriteUInt64LittleEndian(span, value);
        buffer.Advance(8);
    }

    public void WriteVarBytes(ReadOnlySpan<byte> value)
    {
        WriteUInt64((ulong) value.Length);
        WriteBytes(value);
    }
}
