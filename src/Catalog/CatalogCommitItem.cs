﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace NuGet.Services.Metadata.Catalog
{
    /// <summary>
    /// Represents a single item in a catalog commit.
    /// </summary>
    public sealed class CatalogCommitItem : IComparable
    {
        private const string _typeKeyword = "@type";

        private CatalogCommitItem(
            Uri uri,
            string commitId,
            DateTime commitTimeStamp,
            IReadOnlyList<string> types,
            IReadOnlyList<Uri> typeUris,
            PackageIdentity packageIdentity)
        {
            Uri = uri;
            CommitId = commitId;
            CommitTimeStamp = commitTimeStamp;
            PackageIdentity = packageIdentity;
            Types = types;
            TypeUris = typeUris;
        }

        public Uri Uri { get; }
        public DateTime CommitTimeStamp { get; }
        public string CommitId { get; }
        public PackageIdentity PackageIdentity { get; }
        public IReadOnlyList<string> Types { get; }
        public IReadOnlyList<Uri> TypeUris { get; }

        public int CompareTo(object obj)
        {
            var other = obj as CatalogCommitItem;

            if (ReferenceEquals(other, null))
            {
                throw new ArgumentException(
                    string.Format(CultureInfo.InvariantCulture, Strings.ArgumentMustBeInstanceOfType, nameof(CatalogCommitItem)),
                    nameof(obj));
            }

            return CommitTimeStamp.CompareTo(other.CommitTimeStamp);
        }

        public static CatalogCommitItem Create(JObject context, JObject commitItem)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (commitItem == null)
            {
                throw new ArgumentNullException(nameof(commitItem));
            }

            var commitTimeStamp = Utils.Deserialize<DateTime>(commitItem, "commitTimeStamp");
            var commitId = Utils.Deserialize<string>(commitItem, "commitId");
            var idUri = Utils.Deserialize<Uri>(commitItem, "@id");
            var packageId = Utils.Deserialize<string>(commitItem, "nuget:id");
            var packageVersion = Utils.Deserialize<string>(commitItem, "nuget:version");
            var packageIdentity = new PackageIdentity(packageId, new NuGetVersion(packageVersion));
            var types = GetTypes(commitItem).ToArray();

            if (!types.Any())
            {
                throw new ArgumentException(
                    string.Format(CultureInfo.InvariantCulture, Strings.NonEmptyPropertyValueRequired, _typeKeyword),
                    nameof(commitItem));
            }

            var typeUris = types.Select(type => Utils.Expand(context, type)).ToArray();

            return new CatalogCommitItem(idUri, commitId, commitTimeStamp, types, typeUris, packageIdentity);
        }

        private static IEnumerable<string> GetTypes(JObject commitItem)
        {
            if (commitItem.TryGetValue(_typeKeyword, out var value))
            {
                if (value is JArray)
                {
                    foreach (JToken typeToken in ((JArray)value).Values())
                    {
                        yield return typeToken.ToString();
                    }
                }
                else
                {
                    yield return value.ToString();
                }
            }
        }
    }
}