/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Npgsql;
using Spark.Engine.Core;
using Spark.Store.PostgreSQL.Tests.Search;
using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Spark.Store.PostgreSQL.Tests;

[Collection("PostgreSQL integration")]
[Trait("Category", "Integration")]
public class PostgresFhirIndexTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;
    private PostgresFhirStore _store;
    private PostgresFhirIndex _index;

    public PostgresFhirIndexTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetAsync();
        _store = new PostgresFhirStore(_fixture.DataSource, _fixture.FhirModel);
        _index = new PostgresFhirIndex(_fixture.DataSource, _fixture.FhirModel);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task SearchAsync_WithoutCriteria_ReturnsCurrentVersionsOfTypeNewestFirst()
    {
        await AddAsync("Patient", "p1", "1", Now.AddMinutes(1));
        await AddAsync("Patient", "p2", "1", Now.AddMinutes(2));
        await AddAsync("Patient", "p1", "2", Now.AddMinutes(3));
        await AddAsync("Observation", "o1", "1", Now.AddMinutes(4));

        SearchResults results = await _index.SearchAsync("Patient", new SearchParams());

        Assert.Equal(["Patient/p1/_history/2", "Patient/p2/_history/1"], results);
        Assert.Equal(2, results.MatchCount);
        Assert.False(results.HasIssues);
    }

    [Fact]
    public async Task SearchAsync_ExcludesDeletedResources()
    {
        await AddAsync("Patient", "p1", "1", Now);
        await AddAsync("Patient", "p2", "1", Now);
        await _store.AddAsync(Entry.DELETE(Key.Create("Patient", "p1", "2"), Now.AddMinutes(1)));

        SearchResults results = await _index.SearchAsync("Patient", new SearchParams());

        Assert.Equal(["Patient/p2/_history/1"], results);
    }

    [Fact]
    public async Task SearchAsync_ById_ReturnsMatchingResource()
    {
        await AddAsync("Patient", "p1", "1", Now);
        await AddAsync("Patient", "p2", "1", Now);
        await AddAsync("Observation", "p1", "1", Now);

        SearchResults results = await _index.SearchAsync("Patient", new SearchParams().Add("_id", "p1"));

        Assert.Equal(["Patient/p1/_history/1"], results);
        Assert.Equal("_id=p1", results.UsedParameters);
    }

    [Fact]
    public async Task SearchAsync_ByMultipleIds_ReturnsEachMatch()
    {
        await AddAsync("Patient", "p1", "1", Now.AddMinutes(1));
        await AddAsync("Patient", "p2", "1", Now.AddMinutes(2));
        await AddAsync("Patient", "p3", "1", Now.AddMinutes(3));

        SearchResults results = await _index.SearchAsync("Patient", new SearchParams().Add("_id", "p1,p3,missing"));

        Assert.Equal(["Patient/p3/_history/1", "Patient/p1/_history/1"], results);
    }

    [Fact]
    public async Task SearchAsync_ByUnknownId_ReturnsNothing()
    {
        await AddAsync("Patient", "p1", "1", Now);

        Assert.Empty(await _index.SearchAsync("Patient", new SearchParams().Add("_id", "missing")));
    }

    [Fact]
    public async Task SearchAsync_SortByLastUpdated_ReturnsOldestFirst()
    {
        await AddAsync("Patient", "p1", "1", Now.AddMinutes(2));
        await AddAsync("Patient", "p2", "1", Now.AddMinutes(1));

        SearchParams searchParams = new();
        searchParams.Sort.Add(("_lastUpdated", SortOrder.Ascending));
        SearchResults results = await _index.SearchAsync("Patient", searchParams);

        Assert.Equal(["Patient/p2/_history/1", "Patient/p1/_history/1"], results);
    }

    [Theory]
    [InlineData("_lastUpdated", "2026-09-17", "p2")]
    [InlineData("_lastUpdated", "eq2026-09-17", "p2")]
    [InlineData("_lastUpdated", "ap2026-09-17", "p2")]
    [InlineData("_lastUpdated", "ne2026-09-17", "p1,p3")]
    [InlineData("_lastUpdated", "gt2026-09-17", "p3")]
    [InlineData("_lastUpdated", "sa2026-09-17", "p3")]
    [InlineData("_lastUpdated", "ge2026-09-17", "p2,p3")]
    [InlineData("_lastUpdated", "lt2026-09-17", "p1")]
    [InlineData("_lastUpdated", "eb2026-09-17", "p1")]
    [InlineData("_lastUpdated", "le2026-09-17", "p1,p2")]
    [InlineData("_lastUpdated", "2026-09", "p1,p2,p3")]
    [InlineData("_lastUpdated", "gt1900-01-01", "p1,p2,p3")]
    [InlineData("_lastUpdated", "2026-09-17T12:00:00Z", "p2")]
    [InlineData("_lastUpdated", "2026-09-17T12:00:01Z", "")]
    [InlineData("_lastUpdated", "2026-09-16,2026-09-18", "p1,p3")]
    [InlineData("_lastUpdated:missing", "true", "")]
    [InlineData("_lastUpdated:missing", "false", "p1,p2,p3")]
    public async Task SearchAsync_ByLastUpdated_ComparesWithPrecisionOfValue(string name, string value, string expected)
    {
        await AddAsync("Patient", "p1", "1", Now.AddDays(-1));
        await AddAsync("Patient", "p2", "1", Now);
        await AddAsync("Patient", "p3", "1", Now.AddDays(1));

        SearchResults results = await _index.SearchAsync("Patient", new SearchParams().Add(name, value));

        Assert.Equal(
            expected.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(id => $"Patient/{id}/_history/1"),
            results.Order());
    }

    [Fact]
    public async Task SearchAsync_ByLastUpdatedAndId_CombinesCriteria()
    {
        await AddAsync("Patient", "p1", "1", Now.AddDays(-1));
        await AddAsync("Patient", "p2", "1", Now);

        SearchResults results = await _index.SearchAsync("Patient",
            new SearchParams().Add("_lastUpdated", "ge2026-09-16").Add("_id", "p2"));

        Assert.Equal(["Patient/p2/_history/1"], results);
        Assert.Equal(2, results.UsedCriteria.Count);
    }

    [Fact]
    public async Task SearchAsync_ByInvalidLastUpdated_ThrowsBadRequest()
    {
        SparkException exception = await Assert.ThrowsAsync<SparkException>(
            () => _index.SearchAsync("Patient", new SearchParams().Add("_lastUpdated", "not-a-date")));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
    }

    [Fact]
    public async Task SearchAsync_UnknownParameter_IsIgnoredWithWarning()
    {
        await AddAsync("Patient", "p1", "1", Now);

        SearchResults results = await _index.SearchAsync("Patient", new SearchParams().Add("no-such-parameter", "x"));

        Assert.Equal(["Patient/p1/_history/1"], results);
        Assert.True(results.HasIssues);
        Assert.False(results.HasErrors);
        Assert.Equal(OperationOutcome.IssueSeverity.Warning, Assert.Single(results.Outcome.Issue).Severity);
        Assert.Empty(results.UsedCriteria);
    }

    [Fact]
    public async Task SearchAsync_ParameterNotImplemented_ThrowsNotImplemented()
    {
        SparkException exception = await Assert.ThrowsAsync<SparkException>(
            () => _index.SearchAsync("Patient", new SearchParams().Add("name", "Losvik")));

        Assert.Equal(HttpStatusCode.NotImplemented, exception.StatusCode);
    }

    [Fact]
    public async Task SearchAsync_SortNotImplemented_ThrowsNotImplemented()
    {
        SearchParams searchParams = new();
        searchParams.Sort.Add(("birthdate", SortOrder.Ascending));

        SparkException exception = await Assert.ThrowsAsync<SparkException>(
            () => _index.SearchAsync("Patient", searchParams));

        Assert.Equal(HttpStatusCode.NotImplemented, exception.StatusCode);
    }

    [Fact]
    public async Task CountAsync_CountsMatches()
    {
        await AddAsync("Patient", "p1", "1", Now);
        await AddAsync("Patient", "p1", "2", Now);
        await AddAsync("Patient", "p2", "1", Now);

        Assert.Equal(2, await _index.CountAsync("Patient", new SearchParams()));
        Assert.Equal(1, await _index.CountAsync("Patient", new SearchParams().Add("_id", "p2")));
    }

    [Fact]
    public async Task FindSingleAsync_ReturnsKeyOfOnlyMatch()
    {
        await AddAsync("Patient", "p1", "1", Now);
        await AddAsync("Patient", "p2", "1", Now);

        Key key = await _index.FindSingleAsync("Patient", new SearchParams().Add("_id", "p2"));

        Assert.Equal("Patient/p2/_history/1", key.ToString());
    }

    [Fact]
    public async Task FindSingleAsync_WithoutSingleMatch_ThrowsBadRequest()
    {
        await AddAsync("Patient", "p1", "1", Now);
        await AddAsync("Patient", "p2", "1", Now);

        SparkException none = await Assert.ThrowsAsync<SparkException>(
            () => _index.FindSingleAsync("Patient", new SearchParams().Add("_id", "missing")));
        SparkException many = await Assert.ThrowsAsync<SparkException>(
            () => _index.FindSingleAsync("Patient", new SearchParams()));

        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, many.StatusCode);
    }

    [Fact]
    public async Task CleanAsync_ErasesSearchIndex()
    {
        Patient patient = new() { Id = "p1" };
        patient.Name.Add(new HumanName { Family = "Erased" });
        await new PostgresIndexStore(_fixture.DataSource, _fixture.FhirModel)
            .SaveAsync(await new ResourceIndexer(_fixture.FhirModel).IndexAsync(patient, "p1"));

        await _index.CleanAsync();

        await using NpgsqlCommand command = _fixture.DataSource.CreateCommand("SELECT count(*) FROM search_string");
        Assert.Equal(0L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private Task<Entry> AddAsync(string type, string id, string version, DateTimeOffset when)
    {
        Resource resource = type == "Patient" ? new Patient { Id = id } : new Observation { Id = id };
        Entry entry = Entry.PUT(Key.Create(type, id, version), resource);
        entry.When = when;
        return _store.AddAsync(entry);
    }
}
