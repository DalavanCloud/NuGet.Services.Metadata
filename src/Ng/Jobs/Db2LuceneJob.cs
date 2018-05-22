﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Search;
using Microsoft.Extensions.Logging;
using NuGet.Indexing;
using NuGet.Services.Configuration;
using NuGet.Services.Metadata.Catalog;

namespace Ng.Jobs
{
    public class Db2LuceneJob : NgJob
    {
        private string _connectionString;
        private string _searchAccountName;
        private string _searchApiKey;
        private string _indexName;
        private string _source;
        private Uri _catalogIndexUrl;

        public Db2LuceneJob(ITelemetryService telemetryService, ILoggerFactory loggerFactory) : base(telemetryService, loggerFactory)
        {
        }

        public override string GetUsage()
        {
            return "Usage: ng db2lucene "
                   + $"-{Arguments.ConnectionString} <connectionString> "
                   + $"-{Arguments.SearchAccountName} <searchAccountName> "
                   + $"-{Arguments.SearchApiKey} <searchApiKey> "
                   + $"-{Arguments.IndexName} <searchIndex> "
                   + $"[-{Arguments.Verbose} true|false]";
        }

        protected override void Init(IDictionary<string, string> arguments, CancellationToken cancellationToken)
        {
            _connectionString = arguments.GetOrThrow<string>(Arguments.ConnectionString);
            _searchAccountName = arguments.GetOrThrow<string>(Arguments.SearchAccountName);
            _searchApiKey = arguments.GetOrThrow<string>(Arguments.SearchApiKey);
            _indexName = arguments.GetOrThrow<string>(Arguments.IndexName);

            _catalogIndexUrl = new Uri(_source);
        }
        
        protected override Task RunInternal(CancellationToken cancellationToken)
        {
            var searchCredentials = new SearchCredentials(_searchApiKey);
            var searchClient = new SearchServiceClient(_searchAccountName, searchCredentials);

            Sql2Lucene.Export(_connectionString, searchClient, _indexName, LoggerFactory);

            return Task.FromResult(false);
        }
    }
}
