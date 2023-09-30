using System.Data;
using Miningcore.Mining;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using NLog;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Payments.PaymentSchemes;

/// <summary>
/// SOLO payout scheme implementation
/// </summary>
/// ReSharper disable once InconsistentNaming
public class SOLOPaymentScheme : IPayoutScheme
{
    public SOLOPaymentScheme(
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        IBalanceRepository balanceRepo)
    {
        Contract.RequiresNonNull(shareRepo);
        Contract.RequiresNonNull(blockRepo);
        Contract.RequiresNonNull(balanceRepo);

        this.shareRepo = shareRepo;
        this.blockRepo = blockRepo;
        this.balanceRepo = balanceRepo;
    }

    private readonly IBalanceRepository balanceRepo;
    private readonly IBlockRepository blockRepo;
    private readonly IShareRepository shareRepo;
    private static readonly ILogger logger = LogManager.GetLogger("SOLO Payment", typeof(SOLOPaymentScheme));

    #region IPayoutScheme

    public async Task UpdateBalancesAsync(IDbConnection con, IDbTransaction tx, IMiningPool pool, IPayoutHandler payoutHandler,
        Block block, decimal blockReward, CancellationToken ct)
    {
        var poolConfig = pool.Config;

        // calculate rewards
        var rewards = new Dictionary<string, decimal>();
        var shareCutOffDate = CalculateRewards(block, blockReward, rewards, ct);

        // update balances
        foreach(var address in rewards.Keys)
        {
            var amount = rewards[address];

            if(amount > 0)
            {
                logger.Info(() => $"Crediting {address} with {payoutHandler.FormatAmount(amount)} for block {block.BlockHeight}");

                await balanceRepo.AddAmountAsync(con, tx, poolConfig.Id, address, amount, $"Reward for block {block.BlockHeight}");
            }
        }

        // delete discarded shares
        if(shareCutOffDate.HasValue)
        {
            // warning: some multichains coins like ALPH have none-linear blockHeight, so we CAN NOT discard old shares if older blocks are still pending
            var pendingBlockBefore = await blockRepo.GetBlockBeforeCountAsync(con, poolConfig.Id, new[] { BlockStatus.Pending }, block.Created);
            var cutOffCount = await shareRepo.CountSharesByMinerAsync(con, tx, poolConfig.Id, block.Miner, ct);

            if(cutOffCount > 0 && pendingBlockBefore < 1)
            {
                logger.Info(() => $"Deleting {cutOffCount} discarded shares for {block.Miner}");

                await shareRepo.DeleteSharesByMinerAsync(con, tx, poolConfig.Id, block.Miner, ct);
            }
        }
    }

    #endregion // IPayoutScheme

    private DateTime? CalculateRewards(Block block, decimal blockReward, Dictionary<string, decimal> rewards, CancellationToken ct)
    {
        rewards[block.Miner] = blockReward;

        return block.Created;
    }
}
