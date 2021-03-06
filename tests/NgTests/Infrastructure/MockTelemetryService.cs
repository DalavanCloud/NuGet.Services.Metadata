﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using Moq;
using NuGet.Services.Logging;
using NuGet.Services.Metadata.Catalog;
using NuGet.Versioning;

namespace NgTests.Infrastructure
{
    public sealed class MockTelemetryService : ITelemetryService
    {
        private readonly List<TelemetryCall> _trackDurationCalls = new List<TelemetryCall>();
        private readonly List<TrackMetricCall> _trackMetricCalls = new List<TrackMetricCall>();
        private readonly object _durationCallsSyncObject = new object();
        private readonly object _metricCallsSyncObject = new object();

        public IDictionary<string, string> GlobalDimensions => throw new NotImplementedException();

        public IReadOnlyList<TelemetryCall> TrackDurationCalls => _trackDurationCalls;
        public IReadOnlyList<TrackMetricCall> TrackMetricCalls => _trackMetricCalls;

        public void TrackCatalogIndexReadDuration(TimeSpan duration, Uri uri)
        {
        }

        public void TrackCatalogIndexWriteDuration(TimeSpan duration, Uri uri)
        {
        }

        public IDisposable TrackIndexCommitDuration()
        {
            return TrackDuration(nameof(TrackIndexCommitDuration));
        }

        public void TrackIndexCommitTimeout()
        {
        }

        public void TrackHandlerFailedToProcessPackage(IPackagesContainerHandler handler, string packageId, NuGetVersion packageVersion)
        {
        }

        public void TrackPackageMissingHash(string packageId, NuGetVersion packageVersion)
        {
        }

        public void TrackPackageHasIncorrectHash(string packageId, NuGetVersion packageVersion)
        {
        }

        public void TrackPackageAlreadyHasHash(string packageId, NuGetVersion packageVersion)
        {
        }

        public void TrackPackageHashFixed(string packageId, NuGetVersion packageVersion)
        {
        }

        public DurationMetric TrackDuration(string name, IDictionary<string, string> properties = null)
        {
            lock (_durationCallsSyncObject)
            {
                _trackDurationCalls.Add(new TelemetryCall(name, properties));
            }

            return new DurationMetric(Mock.Of<ITelemetryClient>(), name, properties);
        }

        public void TrackMetric(string name, ulong metric, IDictionary<string, string> properties = null)
        {
            lock (_metricCallsSyncObject)
            {
                _trackMetricCalls.Add(new TrackMetricCall(name, metric, properties));
            }
        }
    }
}