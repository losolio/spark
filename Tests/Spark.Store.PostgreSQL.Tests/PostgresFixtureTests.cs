/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Npgsql;
using System.Threading.Tasks;
using Xunit;

namespace Spark.Store.PostgreSQL.Tests;

[Collection("PostgreSQL integration")]
[Trait("Category", "Integration")]
public class PostgresFixtureTests
{
    private readonly PostgresFixture _fixture;

    public PostgresFixtureTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Container_AcceptsConnections()
    {
        await using NpgsqlCommand command = _fixture.DataSource.CreateCommand("SELECT 1");

        object result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result);
    }
}
