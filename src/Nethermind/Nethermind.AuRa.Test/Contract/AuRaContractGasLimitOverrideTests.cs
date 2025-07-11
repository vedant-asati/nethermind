// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Contract;

public class AuRaContractGasLimitOverrideTests
{
    private const int CorrectHeadGasLimit = 100000000;

    // TestContract:
    // pragma solidity ^0.5.0;
    // contract TestValidatorSet {
    //    function blockGasLimit() public view returns(uint256) {
    //        return 100000000;
    //    }
    // }
    [Test]
    public async Task can_read_block_gas_limit_from_contract()
    {
        using TestGasLimitContractBlockchain chain = await TestContractBlockchain.ForTest<TestGasLimitContractBlockchain, AuRaContractGasLimitOverrideTests>();
        long gasLimit = chain.GasLimitCalculator.GetGasLimit(chain.BlockTree.Head.Header);
        gasLimit.Should().Be(CorrectHeadGasLimit);
    }

    [Test]
    public async Task caches_read_block_gas_limit()
    {
        using TestGasLimitContractBlockchain chain = await TestContractBlockchain.ForTest<TestGasLimitContractBlockchain, AuRaContractGasLimitOverrideTests>();
        chain.GasLimitCalculator.GetGasLimit(chain.BlockTree.Head.Header);
        long? gasLimit = chain.GasLimitOverrideCache.GasLimitCache.Get(chain.BlockTree.Head.Hash);
        gasLimit.Should().Be(CorrectHeadGasLimit);
    }

    [Test]
    public async Task can_validate_gas_limit_correct()
    {
        using TestGasLimitContractBlockchain chain = await TestContractBlockchain.ForTest<TestGasLimitContractBlockchain, AuRaContractGasLimitOverrideTests>();
        bool isValid = ((AuRaContractGasLimitOverride)chain.GasLimitCalculator).IsGasLimitValid(chain.BlockTree.Head.Header, CorrectHeadGasLimit, out _);
        isValid.Should().BeTrue();
    }

    [Test]
    public async Task can_validate_gas_limit_incorrect()
    {
        using TestGasLimitContractBlockchain chain = await TestContractBlockchain.ForTest<TestGasLimitContractBlockchain, AuRaContractGasLimitOverrideTests>();
        bool isValid = ((AuRaContractGasLimitOverride)chain.GasLimitCalculator).IsGasLimitValid(chain.BlockTree.Head.Header, 100000001, out long? expectedGasLimit);
        isValid.Should().BeFalse();
        expectedGasLimit.Should().Be(CorrectHeadGasLimit);
    }

    [Test]
    public async Task skip_validate_gas_limit_before_enabled()
    {
        using TestGasLimitContractBlockchainLateBlockGasLimit chain = await TestContractBlockchain.ForTest<TestGasLimitContractBlockchainLateBlockGasLimit, AuRaContractGasLimitOverrideTests>();
        bool isValid = ((AuRaContractGasLimitOverride)chain.GasLimitCalculator).IsGasLimitValid(chain.BlockTree.Genesis, 100000001, out _);
        isValid.Should().BeTrue();
    }

    public class TestGasLimitContractBlockchain : TestContractBlockchain
    {
        public IGasLimitCalculator GasLimitCalculator { get; private set; }
        public AuRaContractGasLimitOverride.Cache GasLimitOverrideCache { get; private set; }

        protected override BlockProcessor CreateBlockProcessor(IWorldState worldState)
        {
            KeyValuePair<long, Address> blockGasLimitContractTransition = ChainSpec.EngineChainSpecParametersProvider
                .GetChainSpecParameters<AuRaChainSpecEngineParameters>().BlockGasLimitContractTransitions
                .First();
            BlockGasLimitContract gasLimitContract = new(AbiEncoder.Instance, blockGasLimitContractTransition.Value,
                blockGasLimitContractTransition.Key,
                new ReadOnlyTxProcessingEnv(
                    WorldStateManager,
                    BlockTree.AsReadOnly(), SpecProvider, LimboLogs.Instance));

            GasLimitOverrideCache = new AuRaContractGasLimitOverride.Cache();
            GasLimitCalculator = new AuRaContractGasLimitOverride(new[] { gasLimitContract }, GasLimitOverrideCache, false, new FollowOtherMiners(SpecProvider), LimboLogs.Instance);

            return new AuRaBlockProcessor(
                SpecProvider,
                Always.Valid,
                new RewardCalculator(SpecProvider),
                new BlockProcessor.BlockValidationTransactionsExecutor(new ExecuteTransactionProcessorAdapter(TxProcessor), worldState),
                worldState,
                ReceiptStorage,
                new BeaconBlockRootHandler(TxProcessor, worldState),
                LimboLogs.Instance,
                BlockTree,
                NullWithdrawalProcessor.Instance,
                new ExecutionRequestsProcessor(TxProcessor),
                auRaValidator: null,
                gasLimitOverride: GasLimitCalculator as AuRaContractGasLimitOverride,
                preWarmer: CreateBlockCachePreWarmer());
        }

        protected override Task AddBlocksOnStart() => Task.CompletedTask;
    }

    public class TestGasLimitContractBlockchainLateBlockGasLimit : TestGasLimitContractBlockchain
    {
        protected override BlockProcessor CreateBlockProcessor(IWorldState worldState)
        {
            var parameters = ChainSpec.EngineChainSpecParametersProvider
                .GetChainSpecParameters<AuRaChainSpecEngineParameters>();
            KeyValuePair<long, Address> blockGasLimitContractTransition = parameters.BlockGasLimitContractTransitions.First();
            parameters.BlockGasLimitContractTransitions = new Dictionary<long, Address>() { { 10, blockGasLimitContractTransition.Value } };
            return base.CreateBlockProcessor(worldState);
        }
    }
}
