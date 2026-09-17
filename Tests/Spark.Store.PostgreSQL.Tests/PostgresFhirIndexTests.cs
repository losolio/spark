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
using System.Net;
using System.Threading.Tasks;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Spark.Store.PostgreSQL.Tests;

[Collection("PostgreSQL integration")]
[Trait("Category", "Integration")]
public class PostgresFhirIndexTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private PostgresFhirIndex _index;

    public PostgresFhirIndexTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetAsync();
        _index = new PostgresFhirIndex(_fixture.DataSource);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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

    [Fact]
    public async Task SearchAsync_IsNotImplemented()
    {
        SparkException exception = await Assert.ThrowsAsync<SparkException>(
            () => _index.SearchAsync("Patient", new SearchParams()));

        Assert.Equal(HttpStatusCode.NotImplemented, exception.StatusCode);
    }
}
