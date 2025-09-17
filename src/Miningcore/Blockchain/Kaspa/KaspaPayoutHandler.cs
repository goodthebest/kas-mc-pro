using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Autofac;
using AutoMapper;
using Grpc.Core;
using Grpc.Net.Client;
using Miningcore.Blockchain.Kaspa.Configuration;
using Miningcore.Blockchain.Kaspa.Wallet;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using Miningcore.Util;
using Block = Miningcore.Persistence.Model.Block;
using Contract = Miningcore.Contracts.Contract;
using static Miningcore.Util.ActionUtils;
using kaspad = Miningcore.Blockchain.Kaspa.Kaspad;

namespace Miningcore.Blockchain.Kaspa;

[CoinFamily(CoinFamily.Kaspa)]
public class KaspaPayoutHandler : PayoutHandlerBase,
    IPayoutHandler
{
    public KaspaPayoutHandler(
        IComponentContext ctx,
        IConnectionFactory cf,
        IMapper mapper,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        IBalanceRepository balanceRepo,
        IPaymentRepository paymentRepo,
        IMasterClock clock,
        IMessageBus messageBus,
        IRustyKaspaWalletFactory walletFactory) :
        base(cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus)
    {
        Contract.RequiresNonNull(ctx);
        Contract.RequiresNonNull(balanceRepo);
        Contract.RequiresNonNull(paymentRepo);
        Contract.RequiresNonNull(walletFactory);

        this.ctx = ctx;
        this.walletFactory = walletFactory;
    }

    protected readonly IComponentContext ctx;
    private readonly IRustyKaspaWalletFactory walletFactory;
    protected kaspad.KaspadRPC.KaspadRPCClient rpc;
    private IRustyKaspaWallet wallet;
    private string network;
    private KaspaPoolConfigExtra extraPoolConfig;
    private KaspaPaymentProcessingConfigExtra extraPoolPaymentProcessingConfig;
    private bool payoutWarningLogged;
    private KaspaDerivedKey treasuryKey;

    protected override string LogCategory => "Kaspa Payout Handler";
    
    #region IPayoutHandler
    
    public virtual async Task ConfigureAsync(ClusterConfig cc, PoolConfig pc, CancellationToken ct)
    {
        Contract.RequiresNonNull(pc);

        poolConfig = pc;
        clusterConfig = cc;
        extraPoolConfig = pc.Extra.SafeExtensionDataAs<KaspaPoolConfigExtra>();
        extraPoolPaymentProcessingConfig = pc.PaymentProcessing.Extra.SafeExtensionDataAs<KaspaPaymentProcessingConfigExtra>();
        
        logger = LogUtil.GetPoolScopedLogger(typeof(KaspaPayoutHandler), pc);
        
        // extract standard daemon endpoints
        var daemonEndpoints = pc.Daemons
            .Where(x => string.IsNullOrEmpty(x.Category))
            .ToArray();

        rpc = KaspaClientFactory.CreateKaspadRPCClient(daemonEndpoints, extraPoolConfig?.ProtobufDaemonRpcServiceName ?? KaspaConstants.ProtobufDaemonRpcServiceName);
        wallet = walletFactory.Create(rpc);
        
        // we need a stream to communicate with Kaspad
        var stream = rpc.MessageStream(null, null, ct);
        
        var requestNetwork = new kaspad.KaspadMessage();
        requestNetwork.GetCurrentNetworkRequest = new kaspad.GetCurrentNetworkRequestMessage();
        await Guard(() => stream.RequestStream.WriteAsync(requestNetwork),
            ex=> throw new PaymentException($"Error writing a request in the communication stream '{ex.GetType().Name}' : {ex}"));

        var requestServerInfo = new kaspad.KaspadMessage();
        requestServerInfo.GetServerInfoRequest = new kaspad.GetServerInfoRequestMessage();
        await Guard(() => stream.RequestStream.WriteAsync(requestServerInfo),
            ex=> throw new PaymentException($"Error writing a request in the communication stream '{ex.GetType().Name}' : {ex}"));

        var networkReceived = false;
        var serverInfoReceived = false;

        await foreach (var response in stream.ResponseStream.ReadAllAsync(ct))
        {
            switch(response.PayloadCase)
            {
                case kaspad.KaspadMessage.PayloadOneofCase.GetCurrentNetworkResponse:
                    if(!string.IsNullOrEmpty(response.GetCurrentNetworkResponse.Error?.Message))
                        throw new PaymentException($"Daemon reports: {response.GetCurrentNetworkResponse.Error?.Message}");

                    network = response.GetCurrentNetworkResponse.CurrentNetwork;
                    networkReceived = true;
                    break;

                case kaspad.KaspadMessage.PayloadOneofCase.GetServerInfoResponse:
                    if(!string.IsNullOrEmpty(response.GetServerInfoResponse.Error?.Message))
                        throw new PaymentException($"Daemon reports: {response.GetServerInfoResponse.Error?.Message}");

                    if(response.GetServerInfoResponse.HasUtxoIndex != true)
                        throw new PaymentException("kaspad is not running with --utxoindex which is required for wallet operations");

                    serverInfoReceived = true;
                    break;
            }

            if(networkReceived && serverInfoReceived)
                break;
        }
        await stream.RequestStream.CompleteAsync();

        TryInitializeTreasuryKey();

        if(!payoutWarningLogged && clusterConfig.PaymentProcessing?.Enabled == true && poolConfig.PaymentProcessing?.Enabled == true && treasuryKey == null)
        {
            payoutWarningLogged = true;
            logger.Warn(() => $"[{LogCategory}] Automated payouts remain disabled because no treasury mnemonic or seed is configured.");
        }
    }

    private void TryInitializeTreasuryKey()
    {
        var mnemonic = extraPoolConfig?.KaspaMnemonic?.Trim();
        var seed = extraPoolConfig?.KaspaSeed?.Trim();

        if(string.IsNullOrEmpty(mnemonic) && string.IsNullOrEmpty(seed))
            return;

        if(!string.IsNullOrEmpty(mnemonic) && !string.IsNullOrEmpty(seed))
            throw new PaymentException("Kaspa pool extra configuration must specify either KaspaMnemonic or KaspaSeed, not both.");

        if(string.IsNullOrEmpty(network))
            throw new PaymentException("Unable to derive Kaspa treasury key before the network has been identified.");

        var derivationPath = !string.IsNullOrWhiteSpace(extraPoolConfig?.KaspaDerivationPath)
            ? extraPoolConfig.KaspaDerivationPath
            : KaspaTreasuryKeyDeriver.DefaultDerivationPath;

        var kaspaNetwork = KaspaNetworkExtensions.ParseNetworkId(network);

        try
        {
            treasuryKey = !string.IsNullOrEmpty(mnemonic)
                ? KaspaTreasuryKeyDeriver.DeriveFromMnemonic(mnemonic, derivationPath, kaspaNetwork)
                : KaspaTreasuryKeyDeriver.DeriveFromSeed(seed!, derivationPath, kaspaNetwork);
        }
        catch(Exception ex)
        {
            throw new PaymentException($"Failed to derive Kaspa treasury key: {ex.Message}", ex);
        }

        logger.Info(() => $"[{LogCategory}] Derived Kaspa treasury address {treasuryKey.Address}");
        logger.Debug(() => $"[{LogCategory}] Derived Kaspa treasury extended private key {treasuryKey.ExtendedPrivateKey}");
    }

    public KaspaDerivedKey TreasuryKey => treasuryKey;
    
    public virtual async Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct)
    {
        Contract.RequiresNonNull(poolConfig);
        Contract.RequiresNonNull(blocks);

        if(blocks.Length == 0)
            return blocks;

        var coin = poolConfig.Template.As<KaspaCoinTemplate>();
        var pageSize = 100;
        var pageCount = (int) Math.Ceiling(blocks.Length / (double) pageSize);
        var result = new List<Block>();
        // KAS minimum confirmation can change over time so please always aknowledge all those different changes very wisely: https://github.com/kaspanet/rusty-kaspa/blob/master/wallet/core/src/utxo/settings.rs
        int minConfirmations = extraPoolPaymentProcessingConfig?.MinimumConfirmations ?? (network == "mainnet" ? 120 : 110);

        // we need a stream to communicate with Kaspad
        var stream = rpc.MessageStream(null, null, ct);

        for(var i = 0; i < pageCount; i++)
        {
            // get a page full of blocks
            var page = blocks
                .Skip(i * pageSize)
                .Take(pageSize)
                .ToArray();
    
            for(var j = 0; j < page.Length; j++)
            {
                var block = page[j];

                // There is a case scenario:
                // https://github.com/blackmennewstyle/miningcore/issues/191
                // Sadly miners can submit different solutions which will produce the exact same blockHash for the same block
                // We must handle that case carefully here, otherwise we will overpay our miners.
                // Only one of these blocks must will be confirmed, the others will all become Orphans
                uint totalDuplicateBlockBefore = await cf.Run(con => blockRepo.GetPoolDuplicateBlockBeforeCountByPoolHeightAndHashNoTypeAndStatusAsync(con, poolConfig.Id, Convert.ToInt64(block.BlockHeight), block.Hash, new[]
                {
                    BlockStatus.Confirmed,
                    BlockStatus.Orphaned,
                    BlockStatus.Pending
                }, block.Created));

                var request = new kaspad.KaspadMessage();
                request.GetBlockRequest = new kaspad.GetBlockRequestMessage
                {
                    Hash = block.Hash,
                    IncludeTransactions = true,
                };
                await Guard(() => stream.RequestStream.WriteAsync(request),
                    ex=> logger.Debug(ex));
                await foreach (var blockInfo in stream.ResponseStream.ReadAllAsync(ct))
                {
                    // We lost that battle
                    if(!string.IsNullOrEmpty(blockInfo.GetBlockResponse.Error?.Message))
                    {
                        result.Add(block);

                        block.Status = BlockStatus.Orphaned;
                        block.Reward = 0;

                        logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned because it's not the chain");

                        messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                    }
                    // multiple blocks with the exact same height & hash recorded in the database
                    else if(totalDuplicateBlockBefore > 0)
                    {
                        result.Add(block);

                        block.Status = BlockStatus.Orphaned;
                        block.Reward = 0;

                        logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} [{block.Hash}] classified as orphaned because we already have in the database {totalDuplicateBlockBefore} block(s) with the same height and hash");

                        messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                    }
                    else
                    {
                        logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} uses a custom minimum confirmations calculation [{minConfirmations}]");

                        var requestConfirmations = new kaspad.KaspadMessage();
                        requestConfirmations.GetBlocksRequest = new kaspad.GetBlocksRequestMessage
                        {
                            LowHash = (string) block.Hash,
                            IncludeBlocks = false,
                            IncludeTransactions = false,
                        };
                        await Guard(() => stream.RequestStream.WriteAsync(requestConfirmations),
                            ex=> logger.Debug(ex));
                        await foreach (var responseConfirmations in stream.ResponseStream.ReadAllAsync(ct))
                        {
                            logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} [{responseConfirmations.GetBlocksResponse.BlockHashes.Count}]");

                            block.ConfirmationProgress = Math.Min(1.0d, (double) responseConfirmations.GetBlocksResponse.BlockHashes.Count / minConfirmations);
                            break;
                        }

                        result.Add(block);

                        messageBus.NotifyBlockConfirmationProgress(poolConfig.Id, block, coin);
                        
                        // matured and spendable?
                        if(block.ConfirmationProgress >= 1)
                        {
                            
                            // KASPA block reward calculation is a complete nightmare: https://wiki.kaspa.org/en/merging-and-rewards
                            decimal blockReward = 0.0m;
                            
                            var childrenProvideRewards = false;
                            
                            // First: We need the parse the children(s) related to the block reward, because in GhostDAG the child(s) reward(s) the parent
                            foreach(var childrenHash in blockInfo.GetBlockResponse.Block.VerboseData.ChildrenHashes)
                            {
                                logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} contains child: {childrenHash}");

                                var requestChildren = new kaspad.KaspadMessage();
                                requestChildren.GetBlockRequest = new kaspad.GetBlockRequestMessage
                                {
                                    Hash = childrenHash,
                                    IncludeTransactions = true,
                                };
                                await Guard(() => stream.RequestStream.WriteAsync(requestChildren),
                                    ex=> logger.Debug(ex));
                                await foreach (var responseChildren in stream.ResponseStream.ReadAllAsync(ct))
                                {
                                    // we only need the transaction(s) related to the block reward
                                    var childrenBlockRewardTransactions = responseChildren.GetBlockResponse.Block.Transactions
                                        .Where(x => x.Inputs.Count < 1)
                                        .ToList();
                                    
                                    if(childrenBlockRewardTransactions.Count > 0)
                                    {
                                        // We need to know if our initial blockHah is in the redMerges
                                        var mergeSetRedsHashess = responseChildren.GetBlockResponse.Block.VerboseData.MergeSetRedsHashes
                                            .Where(x => x.Contains((string) block.Hash))
                                            .ToList();

                                        // We need to know if our initial blockHah is in the blueMerges
                                        var mergeSetBluesHashes = responseChildren.GetBlockResponse.Block.VerboseData.MergeSetBluesHashes
                                            .Where(x => x.Contains((string) block.Hash))
                                            .ToList();
                                        
                                        if(mergeSetRedsHashess.Count > 0)
                                        {
                                            logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} - block child {responseChildren.GetBlockResponse.Block.Header.DaaScore} [{childrenHash}] provides {FormatAmount(0.0m)}");
                                        }
                                        else if(mergeSetBluesHashes.Count > 0 && responseChildren.GetBlockResponse.Block.VerboseData.IsChainBlock)
                                        {
                                            var childrenPosition = responseChildren.GetBlockResponse.Block.VerboseData.MergeSetBluesHashes.IndexOf((string) block.Hash);
                                            
                                            // Are those rewards going to the pool wallet?
                                            if(childrenBlockRewardTransactions.First().Outputs[childrenPosition].VerboseData.ScriptPublicKeyAddress == poolConfig.Address)
                                            {
                                                childrenProvideRewards = true;

                                                logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} - block child {responseChildren.GetBlockResponse.Block.Header.DaaScore} [{childrenHash}] provides {FormatAmount((decimal) (childrenBlockRewardTransactions.First().Outputs[childrenPosition].Amount / KaspaConstants.SmallestUnit))} => {coin.Symbol} address: {childrenBlockRewardTransactions.First().Outputs[childrenPosition].VerboseData.ScriptPublicKeyAddress} [{poolConfig.Address}]");
                                                blockReward += (decimal) (childrenBlockRewardTransactions.First().Outputs[childrenPosition].Amount / KaspaConstants.SmallestUnit);
                                            }
                                            else
                                                logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} - block child {responseChildren.GetBlockResponse.Block.Header.DaaScore} [{childrenHash}] provides {FormatAmount(0.0m)}");
                                            
                                        }
                                        else
                                            logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} - block child {responseChildren.GetBlockResponse.Block.Header.DaaScore} [{childrenHash}] provides {FormatAmount(0.0m)}");
                                    }
                                    else
                                        logger.Warn(() => $"[{LogCategory}] Block {block.BlockHeight} - block child {responseChildren.GetBlockResponse.Block.Header.DaaScore} [{childrenHash}] does not contain transaction(s) related to the block reward, block maybe will not be unlocked :'(");

                                    break;
                                }
                            }
                            
                            // Hold on, we still have one more thing to check
                            if(blockInfo.GetBlockResponse.Block.VerboseData.IsChainBlock && childrenProvideRewards == false)
                            {
                                // we only need the transaction(s) related to the block reward
                                var blockRewardTransactions = blockInfo.GetBlockResponse.Block.Transactions
                                    .Where(x => x.Inputs.Count < 1)
                                    .ToList();
                                
                                if(blockRewardTransactions.Count > 0)
                                {
                                    // We only need the transactions for the pool wallet
                                    var amounts = blockRewardTransactions.First().Outputs
                                        .Where(x => x.VerboseData.ScriptPublicKeyAddress == poolConfig.Address)
                                        .ToList();

                                    if(amounts.Count > 0)
                                    {
                                        var totalAmount = amounts
                                            .Sum(x => (x.Amount / KaspaConstants.SmallestUnit));
                                        
                                        logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} contains: {FormatAmount(totalAmount)}");
                                        blockReward += (decimal) totalAmount;
                                    }
                                    else
                                        logger.Warn(() => $"[{LogCategory}] Block {block.BlockHeight} coinbase transaction(s) provide(s) {FormatAmount(0.0m)}");
                                }
                                else
                                    logger.Warn(() => $"[{LogCategory}] Block {block.BlockHeight} does not contain transaction(s) related to the block reward, block maybe will not be unlocked :'(");
                            }
                            
                            if(blockReward > 0)
                            {
                                block.Status = BlockStatus.Confirmed;
                                block.ConfirmationProgress = 1;

                                // reset block reward
                                block.Reward = blockReward;

                                logger.Info(() => $"[{LogCategory}] Unlocked block {block.BlockHeight} worth {FormatAmount(block.Reward)}");
                                messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                            }
                            else
                            {
                                logger.Warn(() => $"[{LogCategory}] Block {block.BlockHeight} does not receive any block reward :'(");
                                
                                block.Status = BlockStatus.Orphaned;
                                block.Reward = 0;

                                logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned because no reward has been found");

                                messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                            }
                        }
                    }
                    break;
                }
            }
        }
        await stream.RequestStream.CompleteAsync();

        return result.ToArray();
    }
    
    public virtual async Task PayoutAsync(IMiningPool pool, Balance[] balances, CancellationToken ct)
    {
        Contract.RequiresNonNull(balances);

        var payableBalances = balances
            .Where(x => x.Amount > 0)
            .ToArray();

        if(payableBalances.Length == 0)
            return;

        if(treasuryKey == null)
        {
            const string missingKeyMessage = "Kaspa treasury key is unavailable. Configure a mnemonic or seed to enable automated payouts.";

            logger.Error(() => $"[{LogCategory}] {missingKeyMessage}");
            NotifyPayoutFailure(poolConfig.Id, payableBalances, missingKeyMessage, null);

            throw new PaymentException(missingKeyMessage);
        }

        if(wallet == null)
        {
            const string missingWalletMessage = "Kaspa wallet tooling is not initialized.";

            logger.Error(() => $"[{LogCategory}] {missingWalletMessage}");
            NotifyPayoutFailure(poolConfig.Id, payableBalances, missingWalletMessage, null);

            throw new PaymentException(missingWalletMessage);
        }

        if(string.IsNullOrEmpty(network))
            throw new PaymentException("Kaspa network identifier is unknown. Call ConfigureAsync before processing payouts.");

        var coin = poolConfig.Template.As<KaspaCoinTemplate>();
        kaspad.GetUtxosByAddressesResponseMessage.Types.Entry[] utxos;

        try
        {
            utxos = await wallet.GetUtxosByAddressAsync(poolConfig.Address, ct);
            utxos ??= Array.Empty<kaspad.GetUtxosByAddressesResponseMessage.Types.Entry>();

            var spendable = utxos.Sum(x => (decimal) x.UtxoEntry.Amount / KaspaConstants.SmallestUnit);
            logger.Info(() => $"[{LogCategory}] Wallet currently tracks {utxos.Length} UTXO(s) totalling {FormatAmount(spendable)} for payout address {poolConfig.Address}");
        }
        catch(Exception ex)
        {
            var error = $"Failed to query Kaspa UTXOs: {ex.Message}";
            logger.Error(ex, () => $"[{LogCategory}] {error}");
            NotifyPayoutFailure(poolConfig.Id, payableBalances, error, ex);
            throw new PaymentException(error, ex);
        }

        try
        {
            var result = wallet.BuildSignedTransaction(treasuryKey, network, coin, treasuryKey.Address, payableBalances, utxos);
            var allowOrphans = extraPoolConfig?.AllowOrphanTransactions ?? false;
            var txId = await wallet.SubmitTransactionAsync(result.Transaction, allowOrphans, ct);

            var feeDecimal = (decimal) result.Fee / KaspaConstants.SmallestUnit;

            await PersistPaymentsAsync(payableBalances, txId);

            NotifyPayoutSuccess(poolConfig.Id, payableBalances, new[] {txId}, feeDecimal);

            logger.Info(() => $"[{LogCategory}] Submitted Kaspa payout transaction {txId} paying {FormatAmount(payableBalances.Sum(x => x.Amount))} to {payableBalances.Length} address(es) with a fee of {FormatAmount(feeDecimal)}");
        }
        catch(RustyKaspaWalletException ex)
        {
            logger.Error(ex, () => $"[{LogCategory}] Wallet rejected payout transaction assembly: {ex.Message}");
            NotifyPayoutFailure(poolConfig.Id, payableBalances, ex.Message, ex);
            throw new PaymentException(ex.Message);
        }
        catch(Exception ex)
        {
            logger.Error(ex, () => $"[{LogCategory}] Unexpected Kaspa payout failure: {ex.Message}");
            NotifyPayoutFailure(poolConfig.Id, payableBalances, ex.Message, ex);
            throw;
        }
    }

    public override double AdjustShareDifficulty(double difficulty)
    {
        var coin = poolConfig.Template.As<KaspaCoinTemplate>();

        switch(coin.Symbol)
        {
            case "SPR":

                return difficulty * SpectreConstants.Pow2xDiff1TargetNumZero * (double) SpectreConstants.MinHash;
            default:

                return difficulty * KaspaConstants.Pow2xDiff1TargetNumZero * (double) KaspaConstants.MinHash;
        }
    }

    public double AdjustBlockEffort(double effort)
    {
        var coin = poolConfig.Template.As<KaspaCoinTemplate>();

        switch(coin.Symbol)
        {
            case "SPR":

                return effort * SpectreConstants.Pow2xDiff1TargetNumZero * (double) SpectreConstants.MinHash;
            default:

                return effort * KaspaConstants.Pow2xDiff1TargetNumZero * (double) KaspaConstants.MinHash;
        }
    }
    
    #endregion // IPayoutHandler

    private class PaymentException : Exception
    {
        public PaymentException(string msg) : base(msg)
        {
        }
    }
}