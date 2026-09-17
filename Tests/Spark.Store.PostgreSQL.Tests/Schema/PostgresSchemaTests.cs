/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Npgsql;
using Spark.Store.PostgreSQL.Schema;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Spark.Store.PostgreSQL.Tests.Schema;

public class PostgresSchemaScriptTests
{
    [Fact]
    public void GetScripts_ReturnsInitialScriptFirst()
    {
        IReadOnlyList<(string Name, string Sql)> scripts = PostgresSchema.GetScripts();

        Assert.NotEmpty(scripts);
        Assert.Equal("0001_initial.sql", scripts[0].Name);
        Assert.Contains("CREATE TABLE IF NOT EXISTS resources", scripts[0].Sql);
    }

    [Fact]
    public void GetScripts_ReturnsScriptsInFileNameOrder()
    {
        IReadOnlyList<(string Name, string Sql)> scripts = PostgresSchema.GetScripts();

        Assert.Equal(["0001_initial.sql", "0002_search_index.sql"], scripts.Select(script => script.Name));
    }
}

[Collection("PostgreSQL integration")]
[Trait("Category", "Integration")]
public class PostgresSchemaTests
{
    private readonly PostgresFixture _fixture;

    public PostgresSchemaTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EnsureCreatedAsync_OnEmptyDatabase_CreatesTablesAndRecordsScript()
    {
        await using NpgsqlDataSource dataSource = await _fixture.CreateDatabaseAsync("schema");

        await PostgresSchema.EnsureCreatedAsync(dataSource, TestContext.Current.CancellationToken);

        List<string> tables = await GetTablesAsync(dataSource);
        Assert.Contains(Table.ResourceKeys, tables);
        Assert.Contains(Table.Resources, tables);
        Assert.Contains(Table.Snapshots, tables);
        Assert.Contains(Table.IndexQueue, tables);
        Assert.Contains(Table.DatabaseMigrations, tables);
        Assert.Contains(Table.SearchParams, tables);
        Assert.Contains(Table.SearchString, tables);
        Assert.Contains(Table.SearchToken, tables);
        Assert.Contains(Table.SearchDate, tables);
        Assert.Contains(Table.SearchNumber, tables);
        Assert.Contains(Table.SearchQuantity, tables);
        Assert.Contains(Table.SearchReference, tables);
        Assert.Contains(Table.SearchUri, tables);
        Assert.Contains("btree_gist", await ReadStringsAsync(dataSource, "SELECT extname::text FROM pg_extension"));
        Assert.Equal(["0001_initial.sql", "0002_search_index.sql"], await GetAppliedScriptsAsync(dataSource));
    }

    [Fact]
    public async Task EnsureCreatedAsync_CalledTwice_AppliesEachScriptOnce()
    {
        await using NpgsqlDataSource dataSource = await _fixture.CreateDatabaseAsync("schema");

        await PostgresSchema.EnsureCreatedAsync(dataSource, TestContext.Current.CancellationToken);
        await PostgresSchema.EnsureCreatedAsync(dataSource, TestContext.Current.CancellationToken);

        Assert.Equal(["0001_initial.sql", "0002_search_index.sql"], await GetAppliedScriptsAsync(dataSource));
    }

    [Fact]
    public async Task EnsureCreatedAsync_CalledConcurrently_SucceedsOnAllCallers()
    {
        await using NpgsqlDataSource dataSource = await _fixture.CreateDatabaseAsync("schema");

        // Several Spark nodes starting against the same empty database at once.
        IEnumerable<Task> callers = Enumerable.Range(0, 5)
            .Select(_ => PostgresSchema.EnsureCreatedAsync(dataSource, TestContext.Current.CancellationToken));
        await Task.WhenAll(callers);

        Assert.Equal(["0001_initial.sql", "0002_search_index.sql"], await GetAppliedScriptsAsync(dataSource));
    }

    private static async Task<List<string>> GetTablesAsync(NpgsqlDataSource dataSource)
    {
        return await ReadStringsAsync(dataSource,
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' ORDER BY table_name");
    }

    private static async Task<List<string>> GetAppliedScriptsAsync(NpgsqlDataSource dataSource)
    {
        return await ReadStringsAsync(dataSource, $"SELECT script FROM {Table.SchemaHistory} ORDER BY script");
    }

    private static async Task<List<string>> ReadStringsAsync(NpgsqlDataSource dataSource, string sql)
    {
        List<string> values = [];
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
