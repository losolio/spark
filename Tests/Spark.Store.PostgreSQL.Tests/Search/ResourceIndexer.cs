/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Hl7.Fhir.Model;
using Hl7.Fhir.Specification;
using Spark.Engine.Core;
using Spark.Engine.Model;
using Spark.Engine.Search;
using Spark.Engine.Service.FhirServiceExtensions;
using Spark.Engine.Store.Interfaces;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;

namespace Spark.Store.PostgreSQL.Tests.Search;

/// <summary>
/// Builds the <see cref="IndexValue"/> of a resource with the real <see cref="IndexService"/> and R4
/// element indexer, so that tests run against the index values Spark actually produces.
/// </summary>
internal sealed class ResourceIndexer
{
    private readonly IndexService _indexService;

    public ResourceIndexer(IFhirModel fhirModel)
    {
        _indexService = new IndexService(
            fhirModel,
            new DiscardingIndexStore(),
            new ElementIndexer(fhirModel),
            new ResourceResolver(fhirModel.SupportedResources, new PocoStructureDefinitionSummaryProvider()));
    }

    public Task<IndexValue> IndexAsync(Resource resource, string id = "1")
    {
        resource.Id = id;
        return _indexService.IndexResourceAsync(resource, Key.Create(resource.TypeName, id, "1"));
    }

    private sealed class DiscardingIndexStore : IIndexStore
    {
        public Task SaveAsync(IndexValue indexValue) => Task.CompletedTask;

        public Task DeleteAsync(Entry entry) => Task.CompletedTask;

        public Task CleanAsync() => Task.CompletedTask;
    }
}
