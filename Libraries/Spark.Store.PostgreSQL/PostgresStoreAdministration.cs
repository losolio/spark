/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Npgsql;
using Spark.Engine.Interfaces;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Spark.Store.PostgreSQL;

public class PostgresStoreAdministration : IFhirStoreAdministration
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresStoreAdministration(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    /// <summary>
    /// Erases all resources. The search index is erased as well: its rows refer to resource keys, which
    /// would otherwise point at nothing. Search parameter ids and applied migrations are kept.
    /// </summary>
    public async Task CleanAsync()
    {
        string[] tables = [Table.Resources, Table.ResourceKeys, Table.Snapshots, Table.IndexQueue, .. Table.SearchIndex];

        // Identities are not restarted, so a key that is still cached or queued somewhere can never be
        // handed out again for another resource.
        await using NpgsqlCommand command = _dataSource.CreateCommand($"TRUNCATE TABLE {string.Join(", ", tables)}");
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
