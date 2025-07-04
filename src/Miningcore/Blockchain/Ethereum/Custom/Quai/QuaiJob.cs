using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Crypto.Hashing.Ethash;   // Module for Ethash/ProgPoW (built into Miningcore)
using Miningcore.Stratum;
using Miningcore.Extensions;
using NLog;
using NBitcoin;

namespace Miningcore.Blockchain.Ethereum.Custom.Quai
{
    /// <summary>
    /// Mining job for Quai. Performs hash calculations using the ProgPoW algorithm (Ethash derivative).
    /// </summary>
    public class QuaiJob : EthereumJob
    {

        public QuaiJob(string id, EthereumBlockTemplate blockTemplate, ILogger logger, IEthashLight ethash, int shareMultiplier = 1) : base(id, blockTemplate, logger, ethash, shareMultiplier)
        {

            // Convert target value (hexadecimal string) to internal representation
            string targetHex = blockTemplate.Target;
            if (targetHex.StartsWith("0x"))
                targetHex = targetHex.Substring(2);
            blockTarget = new uint256(targetHex.HexToReverseByteArray());
        }

        /// <summary>
        /// Checks for duplicate nonces submitted by miners.
        /// Throws an exception if the same nonce is submitted by the same worker.
        /// </summary>
        private void RegisterNonce(StratumConnection worker, string nonceHex)
        {
            string nonceLower = nonceHex.ToLowerInvariant();
            if (!workerNonces.TryGetValue(worker.ConnectionId, out var nonceSet))
            {
                nonceSet = new HashSet<string>();
                workerNonces[worker.ConnectionId] = nonceSet;
            }
            if (nonceSet.Contains(nonceLower))
                throw new StratumException(StratumError.MinusOne, "duplicate share");
            nonceSet.Add(nonceLower);
        }

        /// <summary>
        /// Processes share submissions from miners.
        /// Performs ProgPoW hash calculation using nonce, difficulty determination, and block candidate determination.
        /// </summary>
        /// <param name="worker">Connection of the miner who submitted the share</param>
        /// <param name="workerName">Miner name</param>
        /// <param name="nonceHex">64-bit nonce (hexadecimal string)</param>
        /// <param name="ethash">Ethash/ProgPoW calculation engine (built into Miningcore)</param>
    public override async Task<SubmitResult> ProcessShareAsync(StratumConnection worker,
        string workerName, string fullNonceHex, string solution, CancellationToken ct)
    {
            // Duplicate nonce check
            lock (workerNonces)
            {
                RegisterNonce(worker, fullNonceHex);
            }

            // Convert nonce hexadecimal string to ulong
            if (!ulong.TryParse(fullNonceHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong nonce))
                throw new StratumException(StratumError.MinusOne, $"bad nonce {fullNonceHex}");

            // Get DAG based on current block height (for ProgPoW)
                    // get dag/light cache for block
        var dag = await ethash.GetCacheAsync(logger, BlockTemplate.Height, ct);
            if (dag == null)
                throw new StratumException(StratumError.MinusOne, "unable to load DAG");

            // Convert HeaderHash (block header part) to byte array
            byte[] headerHashBytes = BlockTemplate.Header.HexToByteArray();
            // Execute ProgPoW calculation and get mixDigest and result hash
            if (!dag.Compute(logger, headerHashBytes, nonce, out byte[] mixDigest, out byte[] resultBytes))
                throw new StratumException(StratumError.MinusOne, "bad hash (PoW computation failed)");

            // Result hash is big-endian. Convert to little-endian for internal processing
            Array.Reverse(resultBytes);
            var resultValue = new uint256(resultBytes);
            BigInteger resultValueBig = resultBytes.AsSpan().ToBigInteger();
            double shareDiff = (double)BigInteger.Divide(EthereumConstants.BigMaxValue, resultValueBig) / EthereumConstants.Pow2x32;

            // Get miner's current difficulty (reusing EthereumWorkerContext)
            var context = worker.ContextAs<EthereumWorkerContext>();
            double minerDiff = context.Difficulty;
            bool isValidShare = true;
            double ratio = shareDiff / minerDiff * shareM;
            if (ratio < 0.99)
            {
                // If immediately after VarDiff (variable difficulty) change, judge using previous difficulty
                if (context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
                {
                    double prevDiff = context.PreviousDifficulty.Value;
                    ratio = shareDiff / prevDiff;
                    if (ratio < 0.99)
                        isValidShare = false;
                    else
                        minerDiff = prevDiff;
                }
                else
                {
                    isValidShare = false;
                }
            }
            if (!isValidShare)
                throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff:F3} < {minerDiff:F3})");

            // If result hash is less than network target, it's a block candidate
            bool isBlockCandidate = (resultValue <= blockTarget);

            // Create Share object
            var share = new Share
            {
                BlockHeight      = (long) BlockTemplate.Height,
                IpAddress        = worker.RemoteEndpoint?.Address?.ToString(),
                Miner            = context.Miner,
                Worker           = workerName,
                UserAgent        = context.UserAgent,
                Difficulty       = minerDiff * EthereumConstants.Pow2x32,
                IsBlockCandidate = isBlockCandidate
            };

            if (isBlockCandidate)
            {
                // If block candidate, prepare parameters for block submission
                string headerHashHex = BlockTemplate.Header;
                string mixHashHex   = mixDigest.ToHexString(true);
                string nonceHexFull = "0x" + fullNonceHex.ToLowerInvariant();
                share.BlockHash = headerHashHex;
                share.TransactionConfirmationData = $"{nonceHexFull}:{mixHashHex}";
            }

            return new SubmitResult(share);
        }

        /// <summary>
        /// Generates parameters for Stratum notification to miners (mining.notify format).
        /// Since it's in Ethereum-compatible format, returns JobID, seed, header, and clean job flag.
        /// </summary>
        public override object[] GetJobParamsForStratum()
        {
            return new object[]
            {
                Id,
                BlockTemplate.Seed.StripHexPrefix(),   // Remove "0x"
                BlockTemplate.Header.StripHexPrefix(),
                true   // Clean job flag
            };
        }
    }
