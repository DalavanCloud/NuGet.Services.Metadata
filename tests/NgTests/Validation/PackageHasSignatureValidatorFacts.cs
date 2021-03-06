﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using NgTests.Infrastructure;
using NgTests.Validation;
using NuGet.Packaging.Core;
using NuGet.Services.Metadata.Catalog;
using NuGet.Services.Metadata.Catalog.Monitoring;
using NuGet.Services.Metadata.Catalog.Monitoring.Validation.Test.Catalog;
using NuGet.Services.Metadata.Catalog.Monitoring.Validation.Test.Exceptions;
using NuGet.Versioning;
using Xunit;

namespace NgTests
{
    public class PackageHasSignatureValidatorFacts
    {
        public class Constructor
        {
            private readonly ValidatorConfiguration _configuration;

            public Constructor()
            {
                _configuration = new ValidatorConfiguration(packageBaseAddress: "a", requirePackageSignature: true);
            }

            [Fact]
            public void WhenConfigIsNull_Throws()
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => new PackageHasSignatureValidator(
                        config: null,
                        logger: Mock.Of<ILogger<PackageHasSignatureValidator>>()));

                Assert.Equal("config", exception.ParamName);
            }

            [Fact]
            public void WhenLoggerIsNull_Throws()
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => new PackageHasSignatureValidator(
                        _configuration,
                        logger: null));

                Assert.Equal("logger", exception.ParamName);
            }
        }

        public class ShouldRunValidator : FactsBase
        {
            [Fact]
            public void SkipsIfNoEntries()
            {
                var target = CreateTarget();
                var context = CreateValidationContext(catalogEntries: new CatalogIndexEntry[0]);

                Assert.False(target.ShouldRunValidator(context));
            }

            [Fact]
            public void SkipsIfLatestEntryIsDelete()
            {
                var target = CreateTarget();
                var uri = new Uri($"https://nuget.test/{PackageIdentity.Id}");
                var context = CreateValidationContext(
                    catalogEntries: new[]
                    {
                        new CatalogIndexEntry(
                            uri,
                            type: CatalogConstants.NuGetPackageDetails,
                            commitId: Guid.NewGuid().ToString(),
                            commitTs: DateTime.MinValue,
                            packageIdentity: PackageIdentity),
                        new CatalogIndexEntry(
                            uri,
                            type: CatalogConstants.NuGetPackageDelete,
                            commitId: Guid.NewGuid().ToString(),
                            commitTs: DateTime.MinValue.AddDays(1),
                            packageIdentity: PackageIdentity),
                    });

                Assert.False(target.ShouldRunValidator(context));
            }

            [Fact]
            public void RunsIfLatestEntryIsntDelete()
            {
                var target = CreateTarget();
                var uri = new Uri($"https://nuget.test/{PackageIdentity.Id}");
                var context = CreateValidationContext(
                    catalogEntries: new[]
                    {
                        new CatalogIndexEntry(
                            uri,
                            type: CatalogConstants.NuGetPackageDelete,
                            commitId: Guid.NewGuid().ToString(),
                            commitTs: DateTime.MinValue,
                            packageIdentity: PackageIdentity),
                        new CatalogIndexEntry(
                            uri,
                            type: CatalogConstants.NuGetPackageDetails,
                            commitId: Guid.NewGuid().ToString(),
                            commitTs: DateTime.MinValue.AddDays(1),
                            packageIdentity: PackageIdentity),
                    });

                Assert.True(target.ShouldRunValidator(context));
            }
        }

        public class RunValidatorAsync : FactsBase
        {
            [Fact]
            public async Task ReturnsGracefullyIfLatestLeafHasSignatureFile()
            {
                // Arrange
                var target = CreateTarget();
                var context = CreateValidationContext(
                    catalogEntries: new[]
                    {
                        new CatalogIndexEntry(
                            uri: new Uri("https://nuget.test/a.json"),
                            type: CatalogConstants.NuGetPackageDetails,
                            commitId: Guid.NewGuid().ToString(),
                            commitTs: DateTime.MinValue,
                            packageIdentity: PackageIdentity),
                        new CatalogIndexEntry(
                            uri: new Uri("https://nuget.test/b.json"),
                            type: CatalogConstants.NuGetPackageDetails,
                            commitId: Guid.NewGuid().ToString(),
                            commitTs: DateTime.MinValue.AddDays(1),
                            packageIdentity: PackageIdentity),
                    });

                AddCatalogLeaf("/a.json", new CatalogLeaf
                {
                    PackageEntries = new[]
                    {
                        new PackageEntry { FullName = "hello.txt" }
                    }
                });

                AddCatalogLeaf("/b.json", new CatalogLeaf
                {
                    PackageEntries = new[]
                    {
                        new PackageEntry { FullName = "hello.txt" },
                        new PackageEntry { FullName = ".signature.p7s" }
                    }
                });

                // Act & Assert
                await target.RunValidatorAsync(context);
            }

            [Fact]
            public async Task ThrowsIfLatestLeafIsMissingASignatureFile()
            {
                // Arrange
                var malformedUri = new Uri("https://nuget.test/b.json");

                var target = CreateTarget();
                var context = CreateValidationContext(
                    catalogEntries: new[]
                    {
                        new CatalogIndexEntry(
                            uri: new Uri("https://nuget.test/a.json"),
                            type: CatalogConstants.NuGetPackageDetails,
                            commitId: Guid.NewGuid().ToString(),
                            commitTs: DateTime.MinValue,
                            packageIdentity: PackageIdentity),
                        new CatalogIndexEntry(
                            uri: malformedUri,
                            type: CatalogConstants.NuGetPackageDetails,
                            commitId: Guid.NewGuid().ToString(),
                            commitTs: DateTime.MinValue.AddDays(1),
                            packageIdentity: PackageIdentity),
                    });

                AddCatalogLeaf("/a.json", new CatalogLeaf
                {
                    PackageEntries = new[]
                    {
                        new PackageEntry { FullName = ".signature.p7s" }
                    }
                });

                AddCatalogLeaf("/b.json", new CatalogLeaf
                {
                    PackageEntries = new[]
                    {
                        new PackageEntry { FullName = "hello.txt" }
                    }
                });

                // Act & Assert
                var e = await Assert.ThrowsAsync<MissingPackageSignatureFileException>(() => target.RunValidatorAsync(context));

                Assert.Same(malformedUri, e.CatalogEntry);
            }

            [Fact]
            public async Task ThrowsIfLeafPackageEntriesIsMissing()
            {
                // Arrange
                var malformedUri = new Uri("https://nuget.test/a.json");

                var target = CreateTarget();
                var context = CreateValidationContext(
                    catalogEntries: new[]
                    {
                        new CatalogIndexEntry(
                            uri: malformedUri,
                            type: CatalogConstants.NuGetPackageDetails,
                            commitId: Guid.NewGuid().ToString(),
                            commitTs: DateTime.MinValue,
                            packageIdentity: PackageIdentity),
                    });

                AddCatalogLeaf("/a.json", "{ 'this': 'is missing the packageEntries field' }");

                // Act & Assert
                var e = await Assert.ThrowsAsync<InvalidOperationException>(() => target.RunValidatorAsync(context));

                Assert.Equal("Catalog leaf is missing the 'packageEntries' property", e.Message);
            }

            [Fact]
            public async Task ThrowsIfLeafPackageEntriesIsMalformed()
            {
                // Arrange
                var malformedUri = new Uri("https://nuget.test/a.json");

                var target = CreateTarget();
                var context = CreateValidationContext(
                    catalogEntries: new[]
                    {
                        new CatalogIndexEntry(
                            uri: malformedUri,
                            type: CatalogConstants.NuGetPackageDetails,
                            commitId: Guid.NewGuid().ToString(),
                            commitTs: DateTime.MinValue,
                            packageIdentity: PackageIdentity),
                    });

                AddCatalogLeaf("/a.json", "{ 'packageEntries': 'malformed'}");

                // Act & Assert
                var e = await Assert.ThrowsAsync<InvalidOperationException>(() => target.RunValidatorAsync(context));

                Assert.Equal("Catalog leaf's 'packageEntries' property is malformed", e.Message);
            }
        }

        public class FactsBase
        {
            public static readonly PackageIdentity PackageIdentity = new PackageIdentity("TestPackage", NuGetVersion.Parse("1.0.0"));

            protected readonly Mock<ILogger<PackageHasSignatureValidator>> _logger;
            private readonly MockServerHttpClientHandler _mockServer;

            public FactsBase()
            {
                _logger = new Mock<ILogger<PackageHasSignatureValidator>>();
                _mockServer = new MockServerHttpClientHandler();
            }

            protected ValidationContext CreateValidationContext(IEnumerable<CatalogIndexEntry> catalogEntries = null)
            {
                catalogEntries = catalogEntries ?? new CatalogIndexEntry[0];

                var httpClient = new CollectorHttpClient(_mockServer);

                return ValidationContextStub.Create(
                    PackageIdentity,
                    catalogEntries,
                    client: httpClient);
            }

            protected PackageHasSignatureValidator CreateTarget(bool requirePackageSignature = true)
            {
                var config = ValidatorTestUtility.CreateValidatorConfig(requirePackageSignature: requirePackageSignature);

                return new PackageHasSignatureValidator(config, _logger.Object);
            }

            protected void AddCatalogLeaf(string path, CatalogLeaf leaf)
            {
                var jsonSettings = new JsonSerializerSettings
                {
                    ContractResolver = new CamelCasePropertyNamesContractResolver()
                };

                AddCatalogLeaf(path, JsonConvert.SerializeObject(leaf, jsonSettings));
            }

            protected void AddCatalogLeaf(string path, string leafContent)
            {
                _mockServer.SetAction(path, request =>
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(leafContent)
                    });
                });
            }

            public class CatalogLeaf
            {
                public IEnumerable<PackageEntry> PackageEntries { get; set; }
            }
        }
    }
}