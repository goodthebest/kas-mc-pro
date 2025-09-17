using System;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Kaspa;
using Miningcore.Blockchain.Kaspa.Configuration;
using Miningcore.Blockchain.Kaspa.Wallet;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using NLog;
using NSubstitute;
using Xunit;
using kaspad = Miningcore.Blockchain.Kaspa.Kaspad;

namespace Miningcore.Tests.Blockchain.Kaspa;

public class KaspaPayoutHandlerTests
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

    [Fact]
    public async Task PayoutAsync_SubmitsTransactionAndPersistsBalances()
    {
        var componentContext = Substitute.For<IComponentContext>();
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var mapper = Substitute.For<IMapper>();
        var shareRepo = Substitute.For<IShareRepository>();
        var blockRepo = Substitute.For<IBlockRepository>();
        var balanceRepo = Substitute.For<IBalanceRepository>();
        balanceRepo.AddAmountAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string[]>())
            .Returns(Task.FromResult(0));
        var paymentRepo = Substitute.For<IPaymentRepository>();
        paymentRepo.InsertAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), Arg.Any<Payment>())
            .Returns(Task.CompletedTask);
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var messageBus = Substitute.For<IMessageBus>();
        var walletFactory = Substitute.For<IRustyKaspaWalletFactory>();
        var wallet = Substitute.For<IRustyKaspaWallet>();

        var handler = new TestKaspaPayoutHandler(
            componentContext,
            connectionFactory,
            mapper,
            shareRepo,
            blockRepo,
            balanceRepo,
            paymentRepo,
            clock,
            messageBus,
            walletFactory);

        var coin = CreateCoinTemplate();
        var treasuryKey = KaspaTreasuryKeyDeriver.DeriveFromMnemonic(
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
            KaspaTreasuryKeyDeriver.DefaultDerivationPath,
            KaspaNetwork.Mainnet);
        var poolConfig = new PoolConfig
        {
            Id = "kaspa",
            Coin = "kaspa",
            Address = treasuryKey.Address,
            RewardRecipients = Array.Empty<RewardRecipient>(),
            PaymentProcessing = new PoolPaymentProcessingConfig { Enabled = true },
            Template = coin
        };
        var clusterConfig = new ClusterConfig
        {
            PaymentProcessing = new ClusterPaymentProcessingConfig { Enabled = true }
        };
        var extraPoolConfig = new KaspaPoolConfigExtra
        {
            AllowOrphanTransactions = false
        };
        var utxos = new[]
        {
            new kaspad.UtxosByAddressesEntry
            {
                Address = treasuryKey.Address,
                Outpoint = new kaspad.RpcOutpoint { TransactionId = "input", Index = 0 },
                UtxoEntry = new kaspad.RpcUtxoEntry { Amount = (ulong) (KaspaConstants.SmallestUnit * 3m) }
            }
        };
        wallet.GetUtxosByAddressAsync(poolConfig.Address, Arg.Any<CancellationToken>()).Returns(Task.FromResult(utxos));

        var transaction = new kaspad.RpcTransaction();
        var result = new KaspaWalletTransactionResult(transaction, 1000);
        wallet.BuildSignedTransaction(
                treasuryKey,
                Arg.Any<string>(),
                coin,
                treasuryKey.Address,
                Arg.Any<Balance[]>(),
                utxos)
            .Returns(result);
        wallet.SubmitTransactionAsync(transaction, false, Arg.Any<CancellationToken>()).Returns(Task.FromResult("kaspa-tx"));

        ConfigureHandler(handler, poolConfig, clusterConfig, wallet, treasuryKey, "kaspa-mainnet", extraPoolConfig);

        var connection = Substitute.For<IDbConnection>();
        var transactionMock = Substitute.For<IDbTransaction>();
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transactionMock);
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));

        var balances = new[]
        {
            new Balance { PoolId = poolConfig.Id, Address = "kaspa:qpee454h906cyt6pqr5gfegpxx7xjqp79dtwcqz8t698ugulhq8fxg56uaxm9", Amount = 1.5m }
        };

        await handler.PayoutAsync(Substitute.For<IMiningPool>(), balances, CancellationToken.None);

        wallet.Received(1).BuildSignedTransaction(
            treasuryKey,
            "kaspa-mainnet",
            coin,
            treasuryKey.Address,
            Arg.Is<Balance[]>(x => x.SequenceEqual(balances)),
            utxos);
        await wallet.Received(1).SubmitTransactionAsync(transaction, false, Arg.Any<CancellationToken>());
        await paymentRepo.Received(1).InsertAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), Arg.Is<Payment>(p => p.TransactionConfirmationData == "kaspa-tx"));
        await balanceRepo.Received(1).AddAmountAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), poolConfig.Id, balances[0].Address, -balances[0].Amount, Arg.Any<string>(), Arg.Any<string[]>());
        Assert.Single(handler.Successes);
        Assert.Equal("kaspa-tx", handler.Successes[0].TxIds.Single());
        Assert.Empty(handler.Failures);
    }

    [Fact]
    public async Task PayoutAsync_NotifiesFailureOnWalletError()
    {
        var componentContext = Substitute.For<IComponentContext>();
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var mapper = Substitute.For<IMapper>();
        var shareRepo = Substitute.For<IShareRepository>();
        var blockRepo = Substitute.For<IBlockRepository>();
        var balanceRepo = Substitute.For<IBalanceRepository>();
        balanceRepo.AddAmountAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string[]>())
            .Returns(Task.FromResult(0));
        var paymentRepo = Substitute.For<IPaymentRepository>();
        paymentRepo.InsertAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), Arg.Any<Payment>()).Returns(Task.CompletedTask);
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var messageBus = Substitute.For<IMessageBus>();
        var walletFactory = Substitute.For<IRustyKaspaWalletFactory>();
        var wallet = Substitute.For<IRustyKaspaWallet>();

        var handler = new TestKaspaPayoutHandler(
            componentContext,
            connectionFactory,
            mapper,
            shareRepo,
            blockRepo,
            balanceRepo,
            paymentRepo,
            clock,
            messageBus,
            walletFactory);

        var coin = CreateCoinTemplate();
        var treasuryKey = KaspaTreasuryKeyDeriver.DeriveFromMnemonic(
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
            KaspaTreasuryKeyDeriver.DefaultDerivationPath,
            KaspaNetwork.Mainnet);
        var poolConfig = new PoolConfig
        {
            Id = "kaspa",
            Coin = "kaspa",
            Address = treasuryKey.Address,
            RewardRecipients = Array.Empty<RewardRecipient>(),
            PaymentProcessing = new PoolPaymentProcessingConfig { Enabled = true },
            Template = coin
        };
        var clusterConfig = new ClusterConfig
        {
            PaymentProcessing = new ClusterPaymentProcessingConfig { Enabled = true }
        };
        var extraPoolConfig = new KaspaPoolConfigExtra();
        var utxos = Array.Empty<kaspad.UtxosByAddressesEntry>();
        wallet.GetUtxosByAddressAsync(poolConfig.Address, Arg.Any<CancellationToken>()).Returns(Task.FromResult(utxos));
        wallet
            .BuildSignedTransaction(
                treasuryKey,
                Arg.Any<string>(),
                coin,
                treasuryKey.Address,
                Arg.Any<Balance[]>(),
                utxos)
            .Returns(_ => throw new RustyKaspaWalletException("insufficient funds"));

        ConfigureHandler(handler, poolConfig, clusterConfig, wallet, treasuryKey, "kaspa-mainnet", extraPoolConfig);

        var connection = Substitute.For<IDbConnection>();
        var tx = Substitute.For<IDbTransaction>();
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(tx);
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));

        var balances = new[]
        {
            new Balance { PoolId = poolConfig.Id, Address = "kaspa:qpee454h906cyt6pqr5gfegpxx7xjqp79dtwcqz8t698ugulhq8fxg56uaxm9", Amount = 2m }
        };

        await Assert.ThrowsAsync<Exception>(() => handler.PayoutAsync(Substitute.For<IMiningPool>(), balances, CancellationToken.None));
        Assert.Single(handler.Failures);
        await wallet.DidNotReceiveWithAnyArgs().SubmitTransactionAsync(default!, default, default);
    }

    private static void ConfigureHandler(TestKaspaPayoutHandler handler, PoolConfig poolConfig, ClusterConfig clusterConfig, IRustyKaspaWallet wallet, KaspaDerivedKey treasuryKey, string network, KaspaPoolConfigExtra extraConfig)
    {
        SetField(handler, "poolConfig", poolConfig, typeof(PayoutHandlerBase));
        SetField(handler, "clusterConfig", clusterConfig, typeof(PayoutHandlerBase));
        SetField(handler, "logger", LogManager.CreateNullLogger(), typeof(PayoutHandlerBase));
        SetField(handler, "wallet", wallet);
        SetField(handler, "treasuryKey", treasuryKey);
        SetField(handler, "network", network);
        SetField(handler, "extraPoolConfig", extraConfig);
        SetField(handler, "extraPoolPaymentProcessingConfig", new KaspaPaymentProcessingConfigExtra());
    }

    private static void SetField(object target, string field, object value, Type? declaringType = null)
    {
        var type = declaringType ?? target.GetType();
        var info = type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
        info?.SetValue(target, value);
    }

    private class TestKaspaPayoutHandler : KaspaPayoutHandler
    {
        public TestKaspaPayoutHandler(
            IComponentContext ctx,
            IConnectionFactory cf,
            IMapper mapper,
            IShareRepository shareRepo,
            IBlockRepository blockRepo,
            IBalanceRepository balanceRepo,
            IPaymentRepository paymentRepo,
            IMasterClock clock,
            IMessageBus messageBus,
            IRustyKaspaWalletFactory walletFactory)
            : base(ctx, cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus, walletFactory)
        {
        }

        public System.Collections.Generic.List<(Balance[] Balances, string[] TxIds, decimal? Fee)> Successes { get; } = new();
        public System.Collections.Generic.List<(Balance[] Balances, string Error)> Failures { get; } = new();

        protected override void NotifyPayoutSuccess(string poolId, Balance[] balances, string[] txHashes, decimal? txFee)
        {
            Successes.Add((balances, txHashes, txFee));
        }

        protected override void NotifyPayoutFailure(string poolId, Balance[] balances, string error, Exception ex)
        {
            Failures.Add((balances, error));
        }
    }
}
