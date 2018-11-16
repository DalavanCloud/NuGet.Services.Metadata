﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.Search.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NuGet.Services.AzureSearch.Wrappers;

namespace NuGet.Services.AzureSearch
{
    public class BatchPusher : IBatchPusher
    {
        private readonly ISearchIndexClientWrapper _searchIndexClient;
        private readonly ISearchIndexClientWrapper _hijackIndexClient;
        private readonly IVersionListDataClient _versionListDataClient;
        private readonly IOptionsSnapshot<AzureSearchConfiguration> _options;
        private readonly ILogger<BatchPusher> _logger;
        internal readonly Dictionary<string, int> _idReferenceCount;
        internal readonly Queue<IdAndValue<IndexAction<KeyedDocument>>> _searchActions;
        internal readonly Queue<IdAndValue<IndexAction<KeyedDocument>>> _hijackActions;
        internal readonly Dictionary<string, ResultAndAccessCondition<VersionListData>> _versionListDataResults;

        public BatchPusher(
            ISearchIndexClientWrapper searchIndexClient,
            ISearchIndexClientWrapper hijackIndexClient,
            IVersionListDataClient versionListDataClient,
            IOptionsSnapshot<AzureSearchConfiguration> options,
            ILogger<BatchPusher> logger)
        {
            _searchIndexClient = searchIndexClient ?? throw new ArgumentNullException(nameof(searchIndexClient));
            _hijackIndexClient = hijackIndexClient ?? throw new ArgumentNullException(nameof(hijackIndexClient));
            _versionListDataClient = versionListDataClient ?? throw new ArgumentNullException(nameof(versionListDataClient));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _idReferenceCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            _searchActions = new Queue<IdAndValue<IndexAction<KeyedDocument>>>();
            _hijackActions = new Queue<IdAndValue<IndexAction<KeyedDocument>>>();
            _versionListDataResults = new Dictionary<string, ResultAndAccessCondition<VersionListData>>();
        }

        public void EnqueueIndexActions(string packageId, IndexActions indexActions)
        {
            if (_versionListDataResults.ContainsKey(packageId))
            {
                throw new ArgumentException("This package ID has already been enqueued.", nameof(packageId));
            }

            if (!indexActions.Hijack.Any() && !indexActions.Search.Any())
            {
                throw new ArgumentException("There must be at least one index action.", nameof(indexActions));
            }

            foreach (var action in indexActions.Hijack)
            {
                // EnqueueAndIncrement(_hijackActions, packageId, action);
            }

            foreach (var action in indexActions.Search)
            {
                EnqueueAndIncrement(_searchActions, packageId, action);
            }

            _versionListDataResults.Add(packageId, indexActions.VersionListDataResult);
        }

        public async Task PushFullBatchesAsync()
        {
            await PushBatchesAsync(onlyFull: true);
        }

        public async Task FinishAsync()
        {
            await PushBatchesAsync(onlyFull: false);
        }

        private async Task PushBatchesAsync(bool onlyFull)
        {
            // await PushBatchesAsync(_hijackIndexClient, _hijackActions, onlyFull);
            await PushBatchesAsync(_searchIndexClient, _searchActions, onlyFull);
        }

        private async Task PushBatchesAsync(
            ISearchIndexClientWrapper indexClient,
            Queue<IdAndValue<IndexAction<KeyedDocument>>> actions,
            bool onlyFull)
        {
            while ((onlyFull && actions.Count >= _options.Value.AzureSearchBatchSize)
                || (!onlyFull && actions.Count > 0))
            {
                var allFinished = new List<IdAndValue<ResultAndAccessCondition<VersionListData>>>();
                var batch = new List<IndexAction<KeyedDocument>>();

                while (batch.Count < _options.Value.AzureSearchBatchSize && actions.Count > 0)
                {
                    var idAndValue = DequeueAndDecrement(actions, out int newCount);
                    batch.Add(idAndValue.Value);

                    if (newCount == 0)
                    {
                        allFinished.Add(NewIdAndValue(idAndValue.Id, _versionListDataResults[idAndValue.Id]));
                        Guard.Assert(_versionListDataResults.Remove(idAndValue.Id), "The version list data result should have existed.");
                    }
                }

                await IndexAsync(indexClient, batch);

                if (allFinished.Any())
                {
                    var versionListIdSample = allFinished
                       .OrderByDescending(x => x.Value.Result.VersionProperties.Count(v => v.Value.Listed))
                       .Select(x => x.Id)
                       .Take(5)
                       .ToArray();
                    _logger.LogInformation(
                        "Updating {VersionListCount} version lists, including {IdSample}.",
                        allFinished.Count,
                        versionListIdSample);
                    var stopwatch = Stopwatch.StartNew();

                    foreach (var finished in allFinished)
                    {
                        break;
                        _logger.LogDebug("Updating version list for package ID {PackageId}.", finished.Id);
                        await _versionListDataClient.ReplaceAsync(
                            finished.Id,
                            finished.Value.Result,
                            finished.Value.AccessCondition);
                    }

                    stopwatch.Stop();
                    _logger.LogInformation(
                        "Done updating {VersionListCount} version lists (took {Duration}).",
                        allFinished.Count,
                        stopwatch.Elapsed);
                }
            }

            Guard.Assert(
                !_versionListDataResults
                    .Keys
                    .Except(_idReferenceCount.Keys)
                    .Any(),
                "There are some version list data results without reference counts.");
            Guard.Assert(
                !_idReferenceCount
                    .Keys
                    .Except(_versionListDataResults.Keys)
                    .Any(),
                "There are some reference counts without version list data results.");
        }

        private async Task IndexAsync(
            ISearchIndexClientWrapper indexClient,
            IReadOnlyCollection<IndexAction<KeyedDocument>> batch)
        {
            if (batch.Count == 0)
            {
                return;
            }

            if (batch.Count > _options.Value.AzureSearchBatchSize)
            {
                throw new ArgumentException("The provided batch is too large.");
            }

            _logger.LogInformation(
                "Pushing batch of {BatchSize} to index {IndexName}.",
                batch.Count,
                indexClient.IndexName);
            var batchResults = await indexClient.Documents.IndexAsync(new IndexBatch<KeyedDocument>(batch));
            const int errorsToLog = 5;
            var errorCount = 0;
            foreach (var result in batchResults.Results)
            {
                if (!result.Succeeded)
                {
                    if (errorCount < errorsToLog)
                    {
                        _logger.LogError(
                            "Indexing document with key {Key} failed. {StatusCode}: {ErrorMessage}",
                            result.Key,
                            result.StatusCode,
                            result.ErrorMessage);
                    }

                    errorCount++;
                }
            }

            if (errorCount > 0)
            {
                _logger.LogError(
                    "{ErrorCount} errors were found when indexing a batch. {LoggedErrors} were logged.",
                    errorCount,
                    Math.Min(errorCount, errorsToLog));
                throw new InvalidOperationException($"Errors were found when indexing a batch. Up to {errorsToLog} errors get logged.");
            }
        }

        private void EnqueueAndIncrement<T>(Queue<IdAndValue<T>> queue, string id, T value)
        {
            if (_idReferenceCount.TryGetValue(id, out var count))
            {
                Guard.Assert(count >= 1, "The existing reference count should always be greater than zero.");
                _idReferenceCount[id] = count + 1;
            }
            else
            {
                _idReferenceCount[id] = 1;
            }

            queue.Enqueue(NewIdAndValue(id, value));
        }

        private IdAndValue<T> DequeueAndDecrement<T>(Queue<IdAndValue<T>> queue, out int newCount)
        {
            var idAndValue = queue.Dequeue();

            var oldCount = _idReferenceCount[idAndValue.Id];
            newCount = oldCount - 1;
            Guard.Assert(newCount >= 0, "The reference count should never be negative.");

            if (newCount == 0)
            {
                _idReferenceCount.Remove(idAndValue.Id);
            }
            else
            {
                _idReferenceCount[idAndValue.Id] = newCount;
            }

            return idAndValue;
        }

        private IdAndValue<T> NewIdAndValue<T>(string id, T value)
        {
            return new IdAndValue<T>(id, value);
        }

        internal class IdAndValue<T>
        {
            public IdAndValue(string id, T value)
            {
                Id = id ?? throw new ArgumentNullException(nameof(id));
                Value = value;
            }

            public string Id { get; }
            public T Value { get; }
        }
    }
}
