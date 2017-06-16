﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using Lucene.Net.Store;
using Lucene.Net.Store.Azure;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using NuGet.Services.Configuration;
using NuGet.Services.KeyVault;
using NuGet.Services.Metadata.Catalog;
using NuGet.Services.Metadata.Catalog.Persistence;

namespace Ng
{
    public static class CommandHelpers
    {
        public static IDictionary<string, string> GetArguments(string[] args, int start)
        {
            var unprocessedArguments = new Dictionary<string, string>();

            if ((args.Length - 1) % 2 != 0)
            {
                Trace.TraceError("Unexpected number of arguments");
                return null;
            }

            for (var i = start; i < args.Length; i += 2)
            {
                // Remove hyphen from the beginning of the argument name.
                var argumentName = args[i].TrimStart(Arguments.Prefix);
                // Remove quotes (if any) from the start and end of the argument value.
                var argumentValue = args[i + 1].Trim(Arguments.Quote);
                unprocessedArguments.Add(argumentName, argumentValue);
            }

            var secretInjector = GetSecretInjector(unprocessedArguments);

            return new SecretDictionary(secretInjector, unprocessedArguments);
        }

        private static void TraceRequiredArgument(string name)
        {
            Trace.TraceError("Required argument \"{0}\" not provided", name);
        }

        private static ISecretInjector GetSecretInjector(IDictionary<string, string> arguments)
        {
            ISecretReader secretReader;

            var vaultName = arguments.GetOrDefault<string>(Arguments.VaultName);
            if (string.IsNullOrEmpty(vaultName))
            {
                secretReader = new EmptySecretReader();
            }
            else
            {
                var clientId = arguments.GetOrThrow<string>(Arguments.ClientId);
                var certificateThumbprint = arguments.GetOrThrow<string>(Arguments.CertificateThumbprint);
                var storeName = arguments.GetOrDefault(Arguments.StoreName, StoreName.My);
                var storeLocation = arguments.GetOrDefault(Arguments.StoreLocation, StoreLocation.LocalMachine);
                var shouldValidateCert = arguments.GetOrDefault(Arguments.ValidateCertificate, false);

                var keyVaultCertificate = CertificateUtility.FindCertificateByThumbprint(storeName, storeLocation, certificateThumbprint, shouldValidateCert);
                var keyVaultConfig = new KeyVaultConfiguration(vaultName, clientId, keyVaultCertificate);

                secretReader = new CachingSecretReader(new KeyVaultReader(keyVaultConfig),
                    arguments.GetOrDefault(Arguments.RefreshIntervalSec, CachingSecretReader.DefaultRefreshIntervalSec));
            }

            return new SecretInjector(secretReader);
        }

        public static RegistrationStorageFactories CreateRegistrationStorageFactories(IDictionary<string, string> arguments, bool verbose)
        {
            StorageFactory legacyStorageFactory;
            var semVer2StorageFactory = CreateSemVer2StorageFactory(arguments, verbose);

            var storageFactory = CreateStorageFactory(arguments, verbose);
            var compressedStorageFactory = CreateCompressedStorageFactory(arguments, verbose);
            if (compressedStorageFactory != null)
            {
                var secondaryStorageBaseUrlRewriter = new SecondaryStorageBaseUrlRewriter(new List<KeyValuePair<string, string>>
                {
                    // always rewrite storage root url in seconary
                    new KeyValuePair<string, string>(storageFactory.BaseAddress.ToString(), compressedStorageFactory.BaseAddress.ToString())
                });

                var aggregateStorageFactory = new AggregateStorageFactory(
                    storageFactory,
                    new[] { compressedStorageFactory },
                    secondaryStorageBaseUrlRewriter.Rewrite)
                {
                    Verbose = verbose
                };

                legacyStorageFactory = aggregateStorageFactory;
            }
            else
            {
                legacyStorageFactory = storageFactory;
            }

            return new RegistrationStorageFactories(legacyStorageFactory, semVer2StorageFactory);
        }

        public static StorageFactory CreateStorageFactory(IDictionary<string, string> arguments, bool verbose)
        {
            IDictionary<string, string> names = new Dictionary<string, string>
            {
                { Arguments.StorageBaseAddress, Arguments.StorageBaseAddress },
                { Arguments.StorageAccountName, Arguments.StorageAccountName },
                { Arguments.StorageKeyValue, Arguments.StorageKeyValue },
                { Arguments.StorageContainer, Arguments.StorageContainer },
                { Arguments.StoragePath, Arguments.StoragePath },
                { Arguments.AliasBasedAddress, Arguments.AliasBasedAddress },
                { Arguments.WriteStorageSuffix, Arguments.WriteStorageSuffix }
            };

            return CreateStorageFactoryImpl(arguments, names, verbose, compressed: false);
        }

        public static StorageFactory CreateCompressedStorageFactory(IDictionary<string, string> arguments, bool verbose)
        {
            if (!arguments.GetOrDefault(Arguments.UseCompressedStorage, false))
            {
                return null;
            }

            IDictionary<string, string> names = new Dictionary<string, string>
            {
                { Arguments.StorageBaseAddress, Arguments.CompressedStorageBaseAddress },
                { Arguments.StorageAccountName, Arguments.CompressedStorageAccountName },
                { Arguments.StorageKeyValue, Arguments.CompressedStorageKeyValue },
                { Arguments.StorageContainer, Arguments.CompressedStorageContainer },
                { Arguments.StoragePath, Arguments.CompressedStoragePath },
                { Arguments.AliasBasedAddress, Arguments.AliasBasedAddress },
                { Arguments.WriteStorageSuffix, Arguments.WriteStorageSuffix }
            };

            return CreateStorageFactoryImpl(arguments, names, verbose, compressed: true);
        }

        public static StorageFactory CreateSemVer2StorageFactory(IDictionary<string, string> arguments, bool verbose)
        {
            if (!arguments.GetOrDefault(Arguments.UseSemVer2Storage, false))
            {
                return null;
            }

            IDictionary<string, string> names = new Dictionary<string, string>
            {
                { Arguments.StorageBaseAddress, Arguments.SemVer2StorageBaseAddress },
                { Arguments.StorageAccountName, Arguments.SemVer2StorageAccountName },
                { Arguments.StorageKeyValue, Arguments.SemVer2StorageKeyValue },
                { Arguments.StorageContainer, Arguments.SemVer2StorageContainer },
                { Arguments.StoragePath, Arguments.SemVer2StoragePath },
                { Arguments.AliasBasedAddress, Arguments.AliasBasedAddress },
                { Arguments.WriteStorageSuffix, Arguments.WriteStorageSuffix }
            };

            return CreateStorageFactoryImpl(arguments, names, verbose, compressed: true);
        }

        public static StorageFactory CreateSuffixedStorageFactory(string suffix, IDictionary<string, string> arguments, bool verbose)
        {
            if (string.IsNullOrEmpty(suffix))
            {
                throw new ArgumentNullException(nameof(suffix));
            }

            IDictionary<string, string> names = new Dictionary<string, string>
            {
                { Arguments.StorageBaseAddress, Arguments.StorageBaseAddress + suffix },
                { Arguments.StorageAccountName, Arguments.StorageAccountName + suffix },
                { Arguments.StorageKeyValue, Arguments.StorageKeyValue + suffix },
                { Arguments.StorageContainer, Arguments.StorageContainer + suffix },
                { Arguments.StoragePath, Arguments.StoragePath + suffix }
            };

            return CreateStorageFactoryImpl(arguments, names, verbose, compressed: false);
        }

        private static StorageFactory CreateStorageFactoryImpl(IDictionary<string, string> arguments, 
                                                               IDictionary<string, string> argumentNameMap,
                                                               bool verbose,
                                                               bool compressed)
        {
            Uri storageBaseAddress = null;
            var storageBaseAddressStr = arguments.GetOrDefault<string>(argumentNameMap[Arguments.StorageBaseAddress]);
            if (!string.IsNullOrEmpty(storageBaseAddressStr))
            {
                storageBaseAddressStr = storageBaseAddressStr.TrimEnd('/') + "/";
                storageBaseAddress = new Uri(storageBaseAddressStr);
            }
            Uri aliasStorageBaseAddress = null;
            var aliasStorageBaseAddressStr = arguments.GetOrDefault<string>(argumentNameMap[Arguments.AliasBasedAddress]);
            if (!string.IsNullOrEmpty(aliasStorageBaseAddressStr))
            {
                aliasStorageBaseAddressStr = aliasStorageBaseAddressStr.TrimEnd('/') + "/";
                aliasStorageBaseAddress = new Uri(aliasStorageBaseAddressStr);
            }
            var storageType = arguments.GetOrThrow<string>(Arguments.StorageType);

            if (storageType.Equals(Arguments.FileStorageType, StringComparison.InvariantCultureIgnoreCase))
            {
                var storagePath = arguments.GetOrThrow<string>(argumentNameMap[Arguments.StoragePath]);

                if (storageBaseAddress != null)
                {
                    return new FileStorageFactory(storageBaseAddress, storagePath) {Verbose = verbose};
                }

                TraceRequiredArgument(argumentNameMap[Arguments.StorageBaseAddress]);
                return null;
            }

            if (Arguments.AzureStorageType.Equals(storageType, StringComparison.InvariantCultureIgnoreCase))
            {
                var storageAccountName = arguments.GetOrThrow<string>(argumentNameMap[Arguments.StorageAccountName]);
                var storageKeyValue = arguments.GetOrThrow<string>(argumentNameMap[Arguments.StorageKeyValue]);
                var storageContainer = arguments.GetOrThrow<string>(argumentNameMap[Arguments.StorageContainer]);
                var storagePath = arguments.GetOrDefault<string>(argumentNameMap[Arguments.StoragePath]);
                var endpointSuffix = arguments.GetOrDefault<string>(argumentNameMap[Arguments.WriteStorageSuffix]);
                
                var credentials = new StorageCredentials(storageAccountName, storageKeyValue);
                var account = endpointSuffix == null ? 
                                new CloudStorageAccount(credentials, true) :
                                new CloudStorageAccount(credentials, endpointSuffix,  true);

                return new AzureStorageFactory(account, storageContainer, storagePath, storageBaseAddress, aliasStorageBaseAddress)
                            { Verbose = verbose, CompressContent = compressed };
            }
            
            throw new ArgumentException($"Unrecognized storageType \"{storageType}\"");
        }

        public static Lucene.Net.Store.Directory GetLuceneDirectory(IDictionary<string, string> arguments, bool required = true)
        {
            IDictionary<string, string> names = new Dictionary<string, string>
            {
                { Arguments.DirectoryType, Arguments.LuceneDirectoryType },
                { Arguments.Path, Arguments.LucenePath },
                { Arguments.StorageAccountName, Arguments.LuceneStorageAccountName },
                { Arguments.StorageKeyValue, Arguments.LuceneStorageKeyValue },
                { Arguments.StorageContainer, Arguments.LuceneStorageContainer }
            };

            return GetLuceneDirectoryImpl(arguments, names, required);
        }

        public static Lucene.Net.Store.Directory GetCopySrcLuceneDirectory(IDictionary<string, string> arguments, bool required = true)
        {
            IDictionary<string, string> names = new Dictionary<string, string>
            {
                { Arguments.DirectoryType, Arguments.SrcDirectoryType },
                { Arguments.Path, Arguments.SrcPath },
                { Arguments.StorageAccountName, Arguments.SrcStorageAccountName },
                { Arguments.StorageKeyValue, Arguments.SrcStorageKeyValue },
                { Arguments.StorageContainer, Arguments.SrcStorageContainer }
            };

            return GetLuceneDirectoryImpl(arguments, names, required);
        }

        public static Lucene.Net.Store.Directory GetCopyDestLuceneDirectory(IDictionary<string, string> arguments, bool required = true)
        {
            IDictionary<string, string> names = new Dictionary<string, string>
            {
                { Arguments.DirectoryType, Arguments.DestDirectoryType },
                { Arguments.Path, Arguments.DestPath },
                { Arguments.StorageAccountName, Arguments.DestStorageAccountName },
                { Arguments.StorageKeyValue, Arguments.DestStorageKeyValue },
                { Arguments.StorageContainer, Arguments.DestStorageContainer }
            };

            return GetLuceneDirectoryImpl(arguments, names, required);
        }

        public static Lucene.Net.Store.Directory GetLuceneDirectoryImpl(IDictionary<string, string> arguments, IDictionary<string, string> argumentNameMap, bool required = true)
        {
            try
            {
                var luceneDirectoryType = arguments.GetOrThrow<string>(argumentNameMap[Arguments.DirectoryType]);

                if (luceneDirectoryType.Equals(Arguments.FileStorageType, StringComparison.InvariantCultureIgnoreCase))
                {
                    var lucenePath = arguments.GetOrThrow<string>(argumentNameMap[Arguments.Path]);

                    var directoryInfo = new DirectoryInfo(lucenePath);

                    if (directoryInfo.Exists)
                    {
                        return new SimpleFSDirectory(directoryInfo);
                    }

                    directoryInfo.Create();
                    directoryInfo.Refresh();

                    return new SimpleFSDirectory(directoryInfo);
                }
                if (luceneDirectoryType.Equals(Arguments.AzureStorageType, StringComparison.InvariantCultureIgnoreCase))
                {
                    var luceneStorageAccountName = arguments.GetOrThrow<string>(argumentNameMap[Arguments.StorageAccountName]);

                    var luceneStorageKeyValue = arguments.GetOrThrow<string>(argumentNameMap[Arguments.StorageKeyValue]);

                    var luceneStorageContainer = arguments.GetOrThrow<string>(argumentNameMap[Arguments.StorageContainer]);

                    var credentials = new StorageCredentials(luceneStorageAccountName, luceneStorageKeyValue);
                    var account = new CloudStorageAccount(credentials, true);
                    return new AzureDirectory(account, luceneStorageContainer);
                }
                Trace.TraceError("Unrecognized Lucene Directory Type \"{0}\"", luceneDirectoryType);
                return null;
            }
            catch (ArgumentException)
            {
                if (required)
                {
                    throw;
                }

                return null;
            }
        }

        public static Func<HttpMessageHandler> GetHttpMessageHandlerFactory(bool verbose, string catalogBaseAddress = null, string storageBaseAddress = null)
        {
            Func<HttpMessageHandler> defaultHandlerFunc = 
                () => new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };

            Func<HttpMessageHandler> handlerFunc = defaultHandlerFunc;

            if (verbose)
            {
                handlerFunc =
                    () =>
                        catalogBaseAddress != null
                            ? new VerboseHandler(new StorageAccessHandler(catalogBaseAddress, storageBaseAddress, defaultHandlerFunc()))
                            : new VerboseHandler(defaultHandlerFunc());
            }

            return handlerFunc;
        }
    }
}
