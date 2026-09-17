/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Hl7.Fhir.Model;
using Npgsql;
using Spark.Engine.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Spark.Store.PostgreSQL.Tests;

[Collection("PostgreSQL integration")]
[Trait("Category", "Integration")]
public class PostgresSnapshotStoreTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private PostgresSnapshotStore _store;

    public PostgresSnapshotStoreTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetAsync();
        _store = new PostgresSnapshotStore(_fixture.DataSource);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task AddSnapshotAsync_ThenGetSnapshotAsync_RoundTripsSearchSnapshot()
    {
        Snapshot snapshot = Snapshot.Create(
            Bundle.BundleType.Searchset,
            new Uri("http://localhost/fhir/Patient?name=a"),
            CreateKeys(3),
            sortBy: "name",
            count: 20,
            includes: ["Patient:organization"],
            reverseIncludes: ["Observation:subject"],
            elements: ["name", "birthDate"]);

        await _store.AddSnapshotAsync(snapshot);
        Snapshot loaded = await _store.GetSnapshotAsync(snapshot.Id);

        Assert.NotNull(loaded);
        Assert.Equal(snapshot.Id, loaded.Id);
        Assert.Equal(Bundle.BundleType.Searchset, loaded.Type);
        Assert.Equal("http://localhost/fhir/Patient?name=a", loaded.FeedSelfLink);
        Assert.Equal(snapshot.Keys, loaded.Keys);
        Assert.Equal(3, loaded.Count);
        Assert.Equal(20, loaded.CountParam);
        Assert.Equal("name", loaded.SortBy);
        Assert.Equal(["Patient:organization"], loaded.Includes);
        Assert.Equal(["Observation:subject"], loaded.ReverseIncludes);
        Assert.Equal(["name", "birthDate"], loaded.Elements);
        Assert.False(loaded.IsCountOnly);
    }

    [Fact]
    public async Task AddSnapshotAsync_WithoutOptionalValues_RoundTripsNulls()
    {
        Snapshot snapshot = Snapshot.Create(Bundle.BundleType.History, new Uri("_history", UriKind.Relative),
            CreateKeys(1), sortBy: null, count: null, includes: null, reverseIncludes: null, elements: null);

        await _store.AddSnapshotAsync(snapshot);
        Snapshot loaded = await _store.GetSnapshotAsync(snapshot.Id);

        Assert.Equal("_history", loaded.FeedSelfLink);
        Assert.Null(loaded.CountParam);
        Assert.Null(loaded.SortBy);
        Assert.Null(loaded.Includes);
        Assert.Null(loaded.ReverseIncludes);
        Assert.Null(loaded.Elements);
        Assert.Equal(Snapshot.DEFAULT_PAGE_SIZE, loaded.GetPageSize());
    }

    [Fact]
    public async Task GetSnapshotAsync_WithOffset_ReturnsTheWholeSnapshot()
    {
        Snapshot snapshot = Snapshot.Create(Bundle.BundleType.Searchset, new Uri("http://localhost/fhir/Patient"),
            CreateKeys(250), sortBy: null, count: 100, includes: null, reverseIncludes: null, elements: null);
        await _store.AddSnapshotAsync(snapshot);

        Snapshot loaded = await _store.GetSnapshotAsync(snapshot.Id, offset: 200);

        Assert.Equal(250, loaded.Keys.Count);
        Assert.Equal(250, loaded.Count);
        Assert.Equal(0, loaded.StartIndex);
        Assert.True(loaded.InRange(200));
    }

    [Fact]
    public async Task GetSnapshotAsync_CountOnly_RestoresTotalWithoutKeys()
    {
        // Snapshot.CreateCountOnly is internal to Spark.Engine, so the row is written directly.
        await using (NpgsqlCommand insert = _fixture.DataSource.CreateCommand(
            $"INSERT INTO {Table.Snapshots} (id, type, feed_self_link, keys, count, is_count_only, created_at) " +
            "VALUES ('count-only', 'Searchset', 'http://localhost/fhir/Patient?_summary=count', '{}', 42, true, now())"))
        {
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        Snapshot loaded = await _store.GetSnapshotAsync("count-only");

        Assert.True(loaded.IsCountOnly);
        Assert.Equal(42, loaded.Count);
        Assert.Empty(loaded.Keys);
        Assert.Equal("count-only", loaded.Id);
    }

    [Fact]
    public async Task GetSnapshotAsync_UnknownId_ReturnsNull()
    {
        Assert.Null(await _store.GetSnapshotAsync("missing"));
        Assert.Null(await _store.GetSnapshotAsync("missing", offset: 0));
    }

    [Fact]
    public async Task AddSnapshotAsync_SameIdTwice_Throws()
    {
        Snapshot snapshot = Snapshot.Create(Bundle.BundleType.Searchset, new Uri("http://localhost/fhir/Patient"),
            CreateKeys(1), sortBy: null, count: null, includes: null, reverseIncludes: null, elements: null);
        await _store.AddSnapshotAsync(snapshot);

        await Assert.ThrowsAsync<PostgresException>(() => _store.AddSnapshotAsync(snapshot));
    }

    private static List<string> CreateKeys(int count)
    {
        return Enumerable.Range(1, count).Select(i => $"Patient/p{i}/_history/1").ToList();
    }
}
