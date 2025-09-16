using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Contracts;
using Miningcore.Util;
using kaspad = Miningcore.Blockchain.Kaspa.Kaspad;

namespace Miningcore.Blockchain.Kaspa.Wallet;

public interface IRustyKaspaWallet : IDisposable
{
    Task<kaspad.GetUtxosByAddressesResponseMessage.Types.Entry[]> GetUtxosByAddressAsync(string address, CancellationToken ct);

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

    public async Task<kaspad.GetUtxosByAddressesResponseMessage.Types.Entry[]> GetUtxosByAddressAsync(string address, CancellationToken ct)
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

            await foreach(var response in stream.ResponseStream.ReadAllAsync(ct))
            {
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

            await foreach(var response in stream.ResponseStream.ReadAllAsync(ct))
            {
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

public class RustyKaspaWalletFactory : IRustyKaspaWalletFactory
{
    public IRustyKaspaWallet Create(kaspad.KaspadRPC.KaspadRPCClient rpc)
    {
        return new RustyKaspaWallet(rpc);
    }
}
