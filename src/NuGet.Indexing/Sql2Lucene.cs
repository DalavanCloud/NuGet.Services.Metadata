﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.Search;
using Microsoft.Extensions.Logging;

namespace NuGet.Indexing
{
    public class Sql2Lucene
    {
        static PackageDocument CreateDocument(SqlDataReader reader, IDictionary<int, List<string>> packageFrameworks)
        {
            var package = new Dictionary<string, string>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (!reader.IsDBNull(i))
                {
                    string name = reader.GetName(i);
                    object obj = reader.GetValue(i);

                    if (name == "key")
                    {
                        var key = (int)obj;
                        List<string> targetFrameworks;
                        if (packageFrameworks.TryGetValue(key, out targetFrameworks))
                        {
                            package.Add("supportedFrameworks", string.Join("|", targetFrameworks));
                        }
                    }

                    var value = (obj is DateTime) ? ((DateTime)obj).ToUniversalTime().ToString("O") : obj.ToString();

                    package.Add(name, value);
                }
            }

            return DocumentCreator.CreateDocument(package);
        }

        static void IndexBatch(string connectionString, ISearchServiceClient searchClient, string indexName, IDictionary<int, List<string>> packageFrameworks, int beginKey, int endKey)
        {
            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();

                // TODO: This query should be where PackageStatusKey = Available
                var cmdText = @"
                    SELECT
                        Packages.[Key]                          'key',
                        PackageRegistrations.Id                 'id',
                        Packages.[Version]                      'verbatimVersion',
                        Packages.NormalizedVersion              'version',
                        Packages.Title                          'title',
                        Packages.Tags                           'tags',
                        Packages.[Description]                  'description',
                        Packages.DownloadCount                  'downloadCount',
                        Packages.FlattenedAuthors               'authors',
                        Packages.Summary                        'summary',
                        Packages.IconUrl                        'iconUrl',
                        Packages.ProjectUrl                     'projectUrl',
                        Packages.MinClientVersion               'minClientVersion',
                        Packages.ReleaseNotes                   'releaseNotes',
                        Packages.Copyright                      'copyright',
                        Packages.[Language]                     'language',
                        Packages.LicenseUrl                     'licenseUrl',
                        Packages.RequiresLicenseAcceptance      'requireLicenseAcceptance',
                        Packages.[Hash]                         'packageHash',
                        Packages.HashAlgorithm                  'packageHashAlgorithm',
                        Packages.PackageFileSize                'packageSize',
                        Packages.FlattenedDependencies          'flattenedDependencies',
                        Packages.Created                        'created',
                        Packages.LastEdited                     'lastEdited',
                        Packages.Published                      'published',
                        Packages.Listed                         'listed',
                        Packages.SemVerLevelKey                 'semVerLevelKey'
                    FROM Packages
                    INNER JOIN PackageRegistrations ON Packages.PackageRegistrationKey = PackageRegistrations.[Key]
                      AND Packages.[Key] >= @BeginKey
                      AND Packages.[Key] < @EndKey
                    WHERE Packages.Deleted = 0
                    ORDER BY Packages.[Key]
                ";

                var command = new SqlCommand(cmdText, connection);
                command.CommandTimeout = (int)TimeSpan.FromMinutes(15).TotalSeconds;
                command.Parameters.AddWithValue("BeginKey", beginKey);
                command.Parameters.AddWithValue("EndKey", endKey);

                var reader = command.ExecuteReader();

                var batch = 0;

                using (var writer = new AzureSearchIndexWriter(searchClient.Indexes.GetClient(indexName)))
                {
                    while (reader.Read())
                    {
                        var document = CreateDocument(reader, packageFrameworks);

                        writer.AddDocument(document);

                        if (batch++ == 1000)
                        {
                            writer.Commit();
                            batch = 0;
                        }
                    }

                    if (batch > 0)
                    {
                        writer.Commit();
                    }
                }
            }
        }

        static List<Tuple<int, int>> CalculateBatches(string connectionString)
        {
            var batches = new List<Tuple<int, int>>();

            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();

                string cmdText = @"
                    SELECT Packages.[Key]
                    FROM Packages
                    INNER JOIN PackageRegistrations ON Packages.PackageRegistrationKey = PackageRegistrations.[Key]
                    WHERE Packages.Deleted = 0
                    ORDER BY Packages.[Key]
                ";

                var command = new SqlCommand(cmdText, connection);
                command.CommandTimeout = (int)TimeSpan.FromMinutes(15).TotalSeconds;

                var reader = command.ExecuteReader();

                var list = new List<int>();

                while (reader.Read())
                {
                    list.Add(reader.GetInt32(0));
                }

                int batch = 0;

                int beginKey = list.First();
                int endKey = 0;

                foreach (int x in list)
                {
                    endKey = x;

                    if (batch++ == 50000)
                    {
                        batches.Add(Tuple.Create(beginKey, endKey));
                        batch = 0;
                        beginKey = endKey;
                    }
                }

                batches.Add(Tuple.Create(beginKey, endKey + 1));
            }

            return batches;
        }

        static IDictionary<int, List<string>> LoadPackageFrameworks(string connectionString)
        {
            var result = new Dictionary<int, List<string>>();

            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();

                var cmdText = @"SELECT Package_Key, TargetFramework FROM PackageFrameworks";

                var command = new SqlCommand(cmdText, connection);
                command.CommandTimeout = (int)TimeSpan.FromMinutes(15).TotalSeconds;

                var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    if (reader.IsDBNull(0) || reader.IsDBNull(1))
                    {
                        continue;
                    }

                    int packageKey = reader.GetInt32(0);
                    string targetFramework = reader.GetString(1);

                    List<string> targetFrameworks;
                    if (!result.TryGetValue(packageKey, out targetFrameworks))
                    {
                        targetFrameworks = new List<string>();
                        result.Add(packageKey, targetFrameworks);
                    }

                    targetFrameworks.Add(targetFramework);
                }
            }

            return result;
        }

        public static void Export(string sourceConnectionString, SearchServiceClient searchClient, string indexName, ILoggerFactory loggerFactory)
        {
            var logger = loggerFactory.CreateLogger<Sql2Lucene>();
            var stopwatch = new Stopwatch();

            stopwatch.Start();

            var batches = CalculateBatches(sourceConnectionString);
            logger.LogInformation("Calculated {BatchCount} batches (took {BatchCalculationTime} seconds)", batches.Count, stopwatch.Elapsed.TotalSeconds);

            stopwatch.Restart();

            var packageFrameworks = LoadPackageFrameworks(sourceConnectionString);
            logger.LogInformation("Loaded package frameworks (took {PackageFrameworksLoadTime} seconds)", stopwatch.Elapsed.TotalSeconds);

            stopwatch.Restart();

            var tasks = new List<Task>();
            foreach (var batch in batches)
            {
                tasks.Add(Task.Run(() => { IndexBatch(sourceConnectionString, searchClient, indexName, packageFrameworks, batch.Item1, batch.Item2); }));
            }

            try
            {
                Task.WaitAll(tasks.ToArray());
            }
            catch (AggregateException ex)
            {
                logger.LogError("An AggregateException occurred while running batches.", ex);

                throw;
            }

            logger.LogInformation("Indexes generated (took {PartitionIndexGenerationTime} seconds)", stopwatch.Elapsed.TotalSeconds);

            stopwatch.Reset();
        }
    }
}
