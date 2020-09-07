﻿using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Stratis.Bitcoin.Primitives;
using Stratis.Bitcoin.Utilities;
using Stratis.Features.FederatedPeg.Interfaces;
using Stratis.Features.FederatedPeg.Models;
using TracerAttributes;

namespace Stratis.Features.FederatedPeg.SourceChain
{
    [NoTrace]
    public class DepositExtractor : IDepositExtractor
    {
        /// <summary>
        /// This deposit extractor implementation only looks for a very specific deposit format.
        /// Deposits will have 2 outputs when there is no change.
        /// </summary>
        private const int ExpectedNumberOfOutputsNoChange = 2;

        /// <summary> Deposits will have 3 outputs when there is change.</summary>
        private const int ExpectedNumberOfOutputsChange = 3;

        private readonly Script depositScript;
        private readonly IFederatedPegSettings federatedPegSettings;
        private readonly ILogger logger;
        private readonly IOpReturnDataReader opReturnDataReader;

        public DepositExtractor(
            ILoggerFactory loggerFactory,
            IFederatedPegSettings federatedPegSettings,
            IOpReturnDataReader opReturnDataReader)
        {
            this.logger = loggerFactory.CreateLogger(this.GetType().FullName);

            // Note: MultiSigRedeemScript.PaymentScript equals MultiSigAddress.ScriptPubKey
            this.depositScript =
                federatedPegSettings.MultiSigRedeemScript?.PaymentScript ??
                federatedPegSettings.MultiSigAddress?.ScriptPubKey;

            this.federatedPegSettings = federatedPegSettings;
            this.opReturnDataReader = opReturnDataReader;
        }

        /// <inheritdoc />
        public IReadOnlyList<IDeposit> ExtractDepositsFromBlock(Block block, int blockHeight, DepositRetrievalType depositRetrievalType)
        {
            var deposits = new List<IDeposit>();

            // If it's an empty block, there's no deposits inside.
            if (block.Transactions.Count <= 1)
                return deposits;

            uint256 blockHash = block.GetHash();

            foreach (Transaction transaction in block.Transactions)
            {
                IDeposit deposit = this.ExtractDepositFromTransaction(transaction, blockHeight, blockHash, depositRetrievalType);
                if (deposit != null)
                {
                    if (depositRetrievalType == DepositRetrievalType.Faster && deposit.Amount <= this.federatedPegSettings.FasterDepositThresholdAmount)
                        deposits.Add(deposit);

                    if (depositRetrievalType == DepositRetrievalType.Normal && deposit.Amount > this.federatedPegSettings.FasterDepositThresholdAmount)
                        deposits.Add(deposit);
                }
            }

            return deposits;
        }

        /// <inheritdoc />
        public IDeposit ExtractDepositFromTransaction(Transaction transaction, int blockHeight, uint256 blockHash, DepositRetrievalType depositRetrievalType)
        {
            // Coinbases can't have deposits.
            if (transaction.IsCoinBase)
                return null;

            // Deposits have a certain structure.
            if (transaction.Outputs.Count != ExpectedNumberOfOutputsNoChange && transaction.Outputs.Count != ExpectedNumberOfOutputsChange)
                return null;

            var depositsToMultisig = transaction.Outputs.Where(output =>
                output.ScriptPubKey == this.depositScript &&
                output.Value >= FederatedPegSettings.CrossChainTransferMinimum).ToList();

            if (!depositsToMultisig.Any())
                return null;

            if (!this.opReturnDataReader.TryGetTargetAddress(transaction, out string targetAddress))
                return null;

            this.logger.LogDebug("Processing a received deposit transaction with address: {0}. Transaction hash: {1}.", targetAddress, transaction.GetHash());

            return new Deposit(transaction.GetHash(), depositRetrievalType, depositsToMultisig.Sum(o => o.Value), targetAddress, blockHeight, blockHash);
        }

        /// <inheritdoc />
        public MaturedBlockDepositsModel ExtractBlockDeposits(ChainedHeaderBlock blockToExtractDepositsFrom, DepositRetrievalType depositRetrievalType)
        {
            Guard.NotNull(blockToExtractDepositsFrom, nameof(blockToExtractDepositsFrom));

            var maturedBlockModel = new MaturedBlockInfoModel()
            {
                BlockHash = blockToExtractDepositsFrom.ChainedHeader.HashBlock,
                BlockHeight = blockToExtractDepositsFrom.ChainedHeader.Height,
                BlockTime = blockToExtractDepositsFrom.ChainedHeader.Header.Time
            };

            IReadOnlyList<IDeposit> deposits = ExtractDepositsFromBlock(blockToExtractDepositsFrom.Block, blockToExtractDepositsFrom.ChainedHeader.Height, depositRetrievalType);

            return new MaturedBlockDepositsModel(maturedBlockModel, deposits);
        }
    }
}