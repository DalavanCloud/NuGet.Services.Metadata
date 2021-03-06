﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Services.Metadata.Catalog.Monitoring;

namespace NgTests
{
    public class RegistrationListedValidatorTestData : RegistrationLeafValidatorTestData<RegistrationListedValidator>
    {
        protected override RegistrationListedValidator CreateValidator(
            IDictionary<FeedType, SourceRepository> feedToSource,
            ILogger<RegistrationListedValidator> logger)
        {
            var config = ValidatorTestUtility.CreateValidatorConfig();

            return new RegistrationListedValidator(feedToSource, config, logger);
        }

        public override IEnumerable<Func<PackageRegistrationIndexMetadata>> CreateIndexes => new Func<PackageRegistrationIndexMetadata>[]
        {
            () => new PackageRegistrationIndexMetadata() { Listed = true },
            () => new PackageRegistrationIndexMetadata() { Listed = false }
        };

        public override IEnumerable<Func<PackageRegistrationIndexMetadata>> CreateSkippedIndexes => new Func<PackageRegistrationIndexMetadata>[]
        {
            () => null
        };

        public override IEnumerable<Func<PackageRegistrationLeafMetadata>> CreateLeafs => new Func<PackageRegistrationLeafMetadata>[]
        {
            () => new PackageRegistrationLeafMetadata() { Listed = true },
            () => new PackageRegistrationLeafMetadata() { Listed = false }
        };

        public override IEnumerable<Func<PackageRegistrationLeafMetadata>> CreateSkippedLeafs => new Func<PackageRegistrationLeafMetadata>[]
        {
            () => null
        };
    }
}