/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Hl7.Fhir.Model;
using Npgsql;
using Spark.Engine.Core;
using Spark.Store.PostgreSQL.Tests.Search;
using System;
using System.Threading.Tasks;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Spark.Store.PostgreSQL.Tests;

[Collection("PostgreSQL integration")]
[Trait("Category", "Integration")]
public class PostgresStoreAdministrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public PostgresStoreAdministrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task CleanAsync_ErasesResourcesAndSearchIndex()
    {
        Patient patient = new() { Id = "p1" };
        patient.Name.Add(new HumanName { Family = "Erased" });
        await new PostgresFhirStore(_fixture.DataSource, _fixture.FhirModel)
            .AddAsync(Entry.PUT(Key.Create("Patient", "p1", "1"), patient));
        await new PostgresIndexStore(_fixture.DataSource, _fixture.FhirModel)
            .SaveAsync(await new ResourceIndexer(_fixture.FhirModel).IndexAsync(patient, "p1"));

        await new PostgresStoreAdministration(_fixture.DataSource).CleanAsync();

        Assert.Equal(0, await CountAsync("SELECT count(*) FROM resources"));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM resource_keys"));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM search_string"));
        Assert.NotEqual(0, await CountAsync("SELECT count(*) FROM search_params"));
    }

    [Fact]
    public async Task CleanAsync_DoesNotReuseResourceKeys()
    {
        PostgresFhirStore store = new(_fixture.DataSource, _fixture.FhirModel);
        await store.AddAsync(Entry.PUT(Key.Create("Patient", "p1", "1"), new Patient()));
        long before = await CountAsync("SELECT max(id) FROM resource_keys");

        await new PostgresStoreAdministration(_fixture.DataSource).CleanAsync();
        await store.AddAsync(Entry.PUT(Key.Create("Patient", "p2", "1"), new Patient()));

        Assert.True(await CountAsync("SELECT max(id) FROM resource_keys") > before);
    }

    private async Task<long> CountAsync(string sql)
    {
        await using NpgsqlCommand command = _fixture.DataSource.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
