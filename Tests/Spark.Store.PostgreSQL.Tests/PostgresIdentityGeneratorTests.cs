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
public class PostgresIdentityGeneratorTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private PostgresIdentityGenerator _generator;

    public PostgresIdentityGeneratorTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetAsync();
        _generator = new PostgresIdentityGenerator(_fixture.DataSource);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public void NextResourceId_ReturnsUuidVersion7()
    {
        string id = _generator.NextResourceId(new Patient());

        Guid guid = Guid.Parse(id);
        Assert.Equal(36, id.Length);
        Assert.Equal(7, guid.Version);
    }

    [Fact]
    public async Task NextResourceId_SortsByCreationTime()
    {
        string first = _generator.NextResourceId(new Patient());
        await Task.Delay(5, TestContext.Current.CancellationToken);
        string second = _generator.NextResourceId(new Patient());

        Assert.True(string.CompareOrdinal(first, second) < 0, $"{first} should sort before {second}");
    }

    [Fact]
    public void NextVersionId_StartsAtOneAndIncrements()
    {
        Assert.Equal("1", _generator.NextVersionId("Patient", "p1"));
        Assert.Equal("2", _generator.NextVersionId("Patient", "p1"));
        Assert.Equal("3", _generator.NextVersionId("Patient", "p1"));
    }

    [Fact]
    public void NextVersionId_CountsPerResource()
    {
        _generator.NextVersionId("Patient", "p1");
        _generator.NextVersionId("Patient", "p1");

        Assert.Equal("1", _generator.NextVersionId("Patient", "p2"));
        Assert.Equal("1", _generator.NextVersionId("Observation", "p1"));
        Assert.Equal("3", _generator.NextVersionId("Patient", "p1"));
    }

    [Fact]
    public async Task NextVersionId_Concurrently_ReturnsUniqueValues()
    {
        IEnumerable<Task<string>> callers = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => _generator.NextVersionId("Patient", "p1")));

        string[] versions = await Task.WhenAll(callers);

        Assert.Equal(Enumerable.Range(1, 20).Select(i => i.ToString()), versions.OrderBy(int.Parse));
    }

    [Fact]
    public async Task NextVersionId_ThenAddAsync_SharesResourceKey()
    {
        PostgresFhirStore store = new(_fixture.DataSource, _fixture.FhirModel);
        string version = _generator.NextVersionId("Patient", "p1");

        await store.AddAsync(Entry.PUT(Key.Create("Patient", "p1", version), new Patient()));

        await using NpgsqlCommand command = _fixture.DataSource.CreateCommand("SELECT count(*) FROM resource_keys");
        Assert.Equal(1L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Equal("2", _generator.NextVersionId("Patient", "p1"));
    }

    [Fact]
    public void NextVersionId_WithoutResourceType_Throws()
    {
        Assert.Throws<NotSupportedException>(() => _generator.NextVersionId("p1"));
    }
}
