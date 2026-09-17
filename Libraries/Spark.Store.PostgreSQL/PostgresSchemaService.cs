/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Microsoft.Extensions.Hosting;
using Npgsql;
using Spark.Store.PostgreSQL.Schema;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Spark.Store.PostgreSQL;

/// <summary>
/// Creates or upgrades the database schema when the host starts. It runs in <see cref="StartingAsync"/>,
/// before any hosted service is started, so the schema exists before the server accepts requests.
/// </summary>
internal sealed class PostgresSchemaService : IHostedLifecycleService
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresSchemaService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        return PostgresSchema.EnsureCreatedAsync(_dataSource, cancellationToken);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
