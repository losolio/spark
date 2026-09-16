/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Npgsql;
using Spark.Engine.Core;
using System;
using System.Threading.Tasks;
using Testcontainers.PostgreSql;
using Xunit;

namespace Spark.Store.PostgreSQL.Tests;

/// <summary>
/// One PostgreSQL container shared by all integration tests in the collection. Tests that need
/// their own database call <see cref="CreateDatabaseAsync"/>.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string PostgresImage = "postgres:18-alpine";

    private PostgreSqlContainer _container;

    public NpgsqlDataSource DataSource { get; private set; }

    public IFhirModel FhirModel { get; } = new FhirModel();

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder(PostgresImage).Build();
            await _container.StartAsync(TestContext.Current.CancellationToken);

            DataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        }
        catch (Exception exception)
        {
            await DisposeAsync();
            Assert.Skip($"Docker/Testcontainers not available: {exception.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (DataSource != null)
        {
            await DataSource.DisposeAsync();
        }

        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// Creates a new, empty database in the container and returns a data source for it. The caller
    /// owns the returned data source.
    /// </summary>
    public async Task<NpgsqlDataSource> CreateDatabaseAsync(string prefix)
    {
        string databaseName = $"{prefix}_{Guid.NewGuid():N}";

        await using (NpgsqlCommand command = DataSource.CreateCommand($"CREATE DATABASE \"{databaseName}\""))
        {
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        NpgsqlConnectionStringBuilder builder = new(_container.GetConnectionString())
        {
            Database = databaseName
        };

        return NpgsqlDataSource.Create(builder.ConnectionString);
    }
}
