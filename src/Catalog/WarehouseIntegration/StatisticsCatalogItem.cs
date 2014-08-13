﻿using Newtonsoft.Json.Linq;
using NuGet.Services.Metadata.Catalog.Maintenance;
using NuGet.Services.Metadata.Catalog.Persistence;
using System;
using VDS.RDF;

namespace NuGet.Services.Metadata.Catalog.WarehouseIntegration
{
    public class StatisticsCatalogItem : CatalogItem
    {
        static Uri CatalogItemType = new Uri("http://nuget.org/schema#PackageStatisticsPage");

        JArray _data;
        string _pageNumber;
        DateTime _minDownloadTimestamp;
        DateTime _maxDownloadTimestamp;

        public StatisticsCatalogItem(JArray data, string pageNumber, DateTime minDownloadTimestamp, DateTime maxDownloadTimestamp)
        {
            _data = data;
            _pageNumber = pageNumber;
            _minDownloadTimestamp = minDownloadTimestamp;
            _maxDownloadTimestamp = maxDownloadTimestamp;
        }
        public override StorageContent CreateContent(CatalogContext context)
        {
            return new StringStorageContent(_data.ToString(), "application/json");
        }

        public override IGraph CreatePageContent(CatalogContext context)
        {
            Uri resourceUri = new Uri(GetBaseAddress() + GetRelativeAddress());

            Graph graph = new Graph();

            INode subject = graph.CreateUriNode(resourceUri);
            INode count = graph.CreateUriNode(new Uri("http://nuget.org/schema#count"));
            INode minDownloadTimestamp = graph.CreateUriNode(new Uri("http://nuget.org/schema#minDownloadTimestamp"));
            INode maxDownloadTimestamp = graph.CreateUriNode(new Uri("http://nuget.org/schema#maxDownloadTimestamp"));

            graph.Assert(subject, count, graph.CreateLiteralNode(_data.Count.ToString(), Schema.DataTypes.Integer));
            graph.Assert(subject, minDownloadTimestamp, graph.CreateLiteralNode(_minDownloadTimestamp.ToString("O"), Schema.DataTypes.DateTime));
            graph.Assert(subject, maxDownloadTimestamp, graph.CreateLiteralNode(_maxDownloadTimestamp.ToString("O"), Schema.DataTypes.DateTime));

            return graph;
        }

        protected override string GetItemIdentity()
        {
            return _pageNumber;
        }

        public override Uri GetItemType()
        {
            return CatalogItemType;
        }
    }
}
