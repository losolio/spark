/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Hl7.Fhir.Model;
using Spark.Engine.Core;
using Spark.Engine.Store.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Spark.Store.PostgreSQL.Tests;

[Collection("PostgreSQL integration")]
[Trait("Category", "Integration")]
public class PostgresFhirStorePagedReaderTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private PostgresFhirStore _store;
    private PostgresFhirStorePagedReader _reader;

    public PostgresFhirStorePagedReaderTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetAsync();
        _store = new PostgresFhirStore(_fixture.DataSource, _fixture.FhirModel);
        _reader = new PostgresFhirStorePagedReader(_fixture.DataSource, _fixture.FhirModel);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task ReadAsync_OnEmptyStore_ReportsNoRecordsAndNeverCallsBack()
    {
        IPageResult<Entry> result = await _reader.ReadAsync();

        int calls = 0;
        await result.IterateAllPagesAsync(_ =>
        {
            calls++;
            return Task.CompletedTask;
        });

        Assert.Equal(0, result.TotalRecords);
        Assert.Equal(0, result.TotalPages);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ReadAsync_CountsOnlyCurrentVersions()
    {
        await _store.AddAsync(CreatePatient("p1", "1"));
        await _store.AddAsync(CreatePatient("p1", "2"));
        await _store.AddAsync(CreatePatient("p2", "1"));

        IPageResult<Entry> result = await _reader.ReadAsync();

        Assert.Equal(2, result.TotalRecords);
    }

    [Fact]
    public async Task IterateAllPagesAsync_ReturnsCurrentEntriesInPagesOfRequestedSize()
    {
        for (int i = 1; i <= 5; i++)
            await _store.AddAsync(CreatePatient($"p{i}", "1"));

        IPageResult<Entry> result = await _reader.ReadAsync(new FhirStorePageReaderOptions { PageSize = 2 });
        List<IReadOnlyList<Entry>> pages = [];
        await result.IterateAllPagesAsync(page =>
        {
            pages.Add(page);
            return Task.CompletedTask;
        });

        Assert.Equal(3, result.TotalPages);
        Assert.Equal([2, 2, 1], pages.Select(page => page.Count));
        Assert.Equal(
            ["p1", "p2", "p3", "p4", "p5"],
            pages.SelectMany(page => page).Select(entry => entry.Key.ResourceId));
    }

    [Fact]
    public async Task IterateAllPagesAsync_IncludesDeletedCurrentEntries()
    {
        await _store.AddAsync(CreatePatient("p1", "1"));
        await _store.AddAsync(Entry.DELETE(Key.Create("Patient", "p1", "2"), DateTimeOffset.UtcNow));

        IPageResult<Entry> result = await _reader.ReadAsync();
        List<Entry> entries = [];
        await result.IterateAllPagesAsync(page =>
        {
            entries.AddRange(page);
            return Task.CompletedTask;
        });

        Entry entry = Assert.Single(entries);
        Assert.False(entry.IsPresent);
        Assert.Equal("Patient/p1/_history/2", entry.Key.ToString());
    }

    [Fact]
    public async Task ReadAsync_WithoutOptions_UsesDefaultPageSize()
    {
        for (int i = 1; i <= 5; i++)
            await _store.AddAsync(CreatePatient($"p{i}", "1"));

        IPageResult<Entry> result = await _reader.ReadAsync();

        Assert.Equal(1, result.TotalPages);
    }

    [Fact]
    public async Task ReadAsync_WithZeroPageSize_Throws()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _reader.ReadAsync(new FhirStorePageReaderOptions { PageSize = 0 }));
    }

    private static Entry CreatePatient(string id, string version)
    {
        return Entry.PUT(Key.Create("Patient", id, version), new Patient());
    }
}
