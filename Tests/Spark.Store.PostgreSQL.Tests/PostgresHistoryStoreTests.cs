/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Http;
using Spark.Engine.Core;
using System;
using System.Threading.Tasks;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Spark.Store.PostgreSQL.Tests;

[Collection("PostgreSQL integration")]
[Trait("Category", "Integration")]
public class PostgresHistoryStoreTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;
    private PostgresFhirStore _store;
    private PostgresHistoryStore _history;

    public PostgresHistoryStoreTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetAsync();
        _store = new PostgresFhirStore(_fixture.DataSource, _fixture.FhirModel);
        _history = new PostgresHistoryStore(_fixture.DataSource);

        await _store.AddAsync(CreateEntry(new Patient(), "p1", "1", T0));
        await _store.AddAsync(CreateEntry(new Patient(), "p1", "2", T0.AddSeconds(1)));
        await _store.AddAsync(CreateEntry(new Patient(), "p2", "1", T0.AddSeconds(2)));
        await _store.AddAsync(CreateEntry(new Observation(), "o1", "1", T0.AddSeconds(3)));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task HistoryAsync_ForResource_ReturnsItsVersionsNewestFirst()
    {
        Snapshot snapshot = await _history.HistoryAsync(Key.Create("Patient", "p1"), Parameters());

        Assert.Equal(Bundle.BundleType.History, snapshot.Type);
        Assert.Equal("_history", snapshot.FeedSelfLink);
        Assert.Equal(["Patient/p1/_history/2", "Patient/p1/_history/1"], snapshot.Keys);
    }

    [Fact]
    public async Task HistoryAsync_ForType_ReturnsAllVersionsOfThatType()
    {
        Snapshot snapshot = await _history.HistoryAsync("Patient", Parameters());

        Assert.Equal(["Patient/p2/_history/1", "Patient/p1/_history/2", "Patient/p1/_history/1"], snapshot.Keys);
    }

    [Fact]
    public async Task HistoryAsync_ForSystem_ReturnsEveryVersion()
    {
        Snapshot snapshot = await _history.HistoryAsync(Parameters());

        Assert.Equal(
            ["Observation/o1/_history/1", "Patient/p2/_history/1", "Patient/p1/_history/2", "Patient/p1/_history/1"],
            snapshot.Keys);
    }

    [Fact]
    public async Task HistoryAsync_WithSince_ReturnsOnlyVersionsAfterThatInstant()
    {
        Snapshot snapshot = await _history.HistoryAsync(Parameters(since: T0.AddSeconds(1)));

        Assert.Equal(["Observation/o1/_history/1", "Patient/p2/_history/1"], snapshot.Keys);
    }

    [Fact]
    public async Task HistoryAsync_WithSinceInAnotherOffset_ComparesInstants()
    {
        DateTimeOffset since = T0.AddSeconds(1).ToOffset(TimeSpan.FromHours(2));

        Snapshot snapshot = await _history.HistoryAsync(Parameters(since: since));

        Assert.Equal(2, snapshot.Keys.Count);
    }

    [Fact]
    public async Task HistoryAsync_WithCount_KeepsAllKeysAndSetsPageSize()
    {
        Snapshot snapshot = await _history.HistoryAsync(Parameters(count: 2));

        Assert.Equal(4, snapshot.Keys.Count);
        Assert.Equal(2, snapshot.GetPageSize());
    }

    [Fact]
    public async Task HistoryAsync_IncludesDeletedVersions()
    {
        await _store.AddAsync(CreateDelete("Patient", "p2", "2", T0.AddSeconds(10)));

        Snapshot snapshot = await _history.HistoryAsync(Key.Create("Patient", "p2"), Parameters());

        Assert.Equal(["Patient/p2/_history/2", "Patient/p2/_history/1"], snapshot.Keys);
    }

    [Fact]
    public async Task HistoryAsync_ForUnknownResource_ReturnsEmptySnapshot()
    {
        Snapshot snapshot = await _history.HistoryAsync(Key.Create("Patient", "missing"), Parameters());

        Assert.Empty(snapshot.Keys);
        Assert.Equal(0, snapshot.Count);
    }

    [Fact]
    public async Task HistoryAsync_WithoutParameters_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _history.HistoryAsync("Patient", null));
    }

    private static HistoryParameters Parameters(int? count = null, DateTimeOffset? since = null)
    {
        return new HistoryParameters(new DefaultHttpContext().Request) { Count = count, Since = since };
    }

    private static Entry CreateEntry(Resource resource, string id, string version, DateTimeOffset lastUpdated)
    {
        // Entry's constructor stamps "now" on the resource, so the instant has to be set afterwards.
        Entry entry = Entry.PUT(Key.Create(resource.TypeName, id, version), resource);
        entry.When = lastUpdated;
        return entry;
    }

    private static Entry CreateDelete(string type, string id, string version, DateTimeOffset when)
    {
        // Entry.DELETE ignores its time argument and uses "now".
        Entry entry = Entry.DELETE(Key.Create(type, id, version), when);
        entry.When = when;
        return entry;
    }
}
