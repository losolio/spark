/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Npgsql;
using Spark.Engine.Core;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Spark.Store.PostgreSQL.Tests;

[Collection("PostgreSQL integration")]
[Trait("Category", "Integration")]
public class PostgresFhirStoreTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private PostgresFhirStore _store;

    public PostgresFhirStoreTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetAsync();
        _store = new PostgresFhirStore(_fixture.DataSource, _fixture.FhirModel);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task AddAsync_ThenGetAsync_ReturnsStoredResource()
    {
        Entry entry = CreatePatient("p1", "1", family: "Losen");

        Entry stored = await _store.AddAsync(entry);
        Entry loaded = await _store.GetAsync(Key.Create("Patient", "p1"));

        Assert.NotSame(entry, stored);
        Assert.Equal("Patient/p1/_history/1", stored.Key.ToString());
        Assert.NotNull(loaded);
        Assert.Equal(Bundle.HTTPVerb.PUT, loaded.Method);
        Assert.Equal("Patient/p1/_history/1", loaded.Key.ToString());
        Patient patient = Assert.IsType<Patient>(loaded.Resource);
        Assert.Equal("Losen", patient.Name.Single().Family);
        Assert.NotNull(loaded.When);
    }

    [Fact]
    public async Task AddAsync_NewVersion_SupersedesPreviousVersion()
    {
        await _store.AddAsync(CreatePatient("p1", "1", family: "First"));
        await _store.AddAsync(CreatePatient("p1", "2", family: "Second"));

        Entry current = await _store.GetAsync(Key.Create("Patient", "p1"));
        Entry first = await _store.GetAsync(Key.Create("Patient", "p1", "1"));

        Assert.Equal("2", current.Key.VersionId);
        Assert.Equal("Second", ((Patient)current.Resource).Name.Single().Family);
        Assert.Equal("1", first.Key.VersionId);
        Assert.Equal("First", ((Patient)first.Resource).Name.Single().Family);
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM resources WHERE state = 'current'"));
        Assert.Equal(2, await CountAsync("SELECT count(*) FROM resources"));
    }

    [Fact]
    public async Task AddAsync_NewVersion_ReusesResourceKey()
    {
        await _store.AddAsync(CreatePatient("p1", "1"));
        await _store.AddAsync(CreatePatient("p1", "2"));
        await _store.AddAsync(CreatePatient("p2", "1"));

        Assert.Equal(2, await CountAsync("SELECT count(*) FROM resource_keys"));
        Assert.Equal(1, await CountAsync(
            "SELECT count(DISTINCT r.resource_key) FROM resources r " +
            "JOIN resource_keys k ON k.id = r.resource_key WHERE k.type = 'Patient' AND k.resource_id = 'p1'"));
    }

    [Fact]
    public async Task AddAsync_Delete_ReturnsEntryWithoutResource()
    {
        await _store.AddAsync(CreatePatient("p1", "1"));

        await _store.AddAsync(Entry.DELETE(Key.Create("Patient", "p1", "2"), DateTimeOffset.UtcNow));
        Entry current = await _store.GetAsync(Key.Create("Patient", "p1"));

        Assert.NotNull(current);
        Assert.False(current.IsPresent);
        Assert.Equal(Bundle.HTTPVerb.DELETE, current.Method);
        Assert.Null(current.Resource);
        Assert.Equal("Patient/p1/_history/2", current.Key.ToString());
    }

    [Fact]
    public async Task GetAsync_UnknownKey_ReturnsNull()
    {
        Assert.Null(await _store.GetAsync(Key.Create("Patient", "missing")));
        Assert.Null(await _store.GetAsync(Key.Create("Patient", "missing", "1")));
    }

    [Fact]
    public async Task GetAsync_WithMixedKeys_ReturnsCurrentAndSpecificVersions()
    {
        await _store.AddAsync(CreatePatient("p1", "1"));
        await _store.AddAsync(CreatePatient("p1", "2"));
        await _store.AddAsync(CreatePatient("p2", "1"));

        IList<Entry> entries = await _store.GetAsync(
        [
            Key.Create("Patient", "p1"),
            Key.Create("Patient", "p2", "1"),
            Key.Create("Patient", "p1", "1"),
            Key.Create("Patient", "missing"),
        ]);

        Assert.Equal(
            ["Patient/p1/_history/1", "Patient/p1/_history/2", "Patient/p2/_history/1"],
            entries.Select(entry => entry.Key.ToString()).OrderBy(key => key));
    }

    [Fact]
    public async Task GetAsync_WithEmptyKeys_ReturnsEmptyList()
    {
        Assert.Empty(await _store.GetAsync([]));
    }

    [Fact]
    public async Task GetAsync_WithElements_ReturnsSubsettedResource()
    {
        await _store.AddAsync(CreatePatient("p1", "1", family: "Losvik", birthDate: "1980-01-01"));

        IList<Entry> entries = await _store.GetAsync([Key.Create("Patient", "p1")], ["birthDate"]);

        Patient patient = Assert.IsType<Patient>(Assert.Single(entries).Resource);
        Assert.Equal("p1", patient.Id);
        Assert.Equal("1980-01-01", patient.BirthDate);
        Assert.Empty(patient.Name);
        Assert.Contains(patient.Meta.Tag, tag => tag.Code == "SUBSETTED");
    }

    [Fact]
    public async Task GetAsync_WithoutElements_DoesNotTagResource()
    {
        await _store.AddAsync(CreatePatient("p1", "1", family: "Losvik"));

        IList<Entry> entries = await _store.GetAsync([Key.Create("Patient", "p1")]);

        Patient patient = Assert.IsType<Patient>(Assert.Single(entries).Resource);
        Assert.Single(patient.Name);
        Assert.DoesNotContain(patient.Meta?.Tag ?? [], tag => tag.Code == "SUBSETTED");
    }

    [Fact]
    public async Task AddAsync_Concurrently_KeepsExactlyOneCurrentVersion()
    {
        IEnumerable<Task<Entry>> writers = Enumerable.Range(1, 10)
            .Select(version => _store.AddAsync(CreatePatient("p1", version.ToString())));

        await Task.WhenAll(writers);

        Assert.Equal(10, await CountAsync("SELECT count(*) FROM resources"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM resources WHERE state = 'current'"));
        Entry current = await _store.GetAsync(Key.Create("Patient", "p1"));
        Assert.NotNull(current.Resource);
    }

    [Fact]
    public async Task AddAsync_SameVersionTwice_Throws()
    {
        await _store.AddAsync(CreatePatient("p1", "1"));

        await Assert.ThrowsAsync<PostgresException>(() => _store.AddAsync(CreatePatient("p1", "1")));
    }

    [Fact]
    public async Task AddAsync_WithKeyWithoutVersion_Throws()
    {
        Entry entry = Entry.PUT(Key.Create("Patient", "p1"), new Patient());

        await Assert.ThrowsAsync<ArgumentException>(() => _store.AddAsync(entry));
    }

    private static Entry CreatePatient(string id, string version, string family = null, string birthDate = null)
    {
        Patient patient = new()
        {
            BirthDate = birthDate,
            Meta = new Meta { LastUpdated = DateTimeOffset.UtcNow },
        };
        if (family != null)
            patient.Name.Add(new HumanName { Family = family });

        return Entry.PUT(Key.Create("Patient", id, version), patient);
    }

    private async Task<long> CountAsync(string sql)
    {
        await using NpgsqlCommand command = _fixture.DataSource.CreateCommand(sql);
        return (long)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }
}
