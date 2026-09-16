/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Npgsql;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Spark.Store.PostgreSQL.Schema;

/// <summary>
/// Creates and upgrades the database schema from the SQL scripts embedded in this assembly.
/// Scripts run in file name order and each script runs once; the applied scripts are recorded
/// in the <c>schema_history</c> table.
/// </summary>
public static class PostgresSchema
{
    private const string ScriptPrefix = "Schema/";

    public static async Task EnsureCreatedAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Spark nodes that start at the same time take turns here. CREATE ... IF NOT EXISTS is not
        // safe under concurrent sessions, and the lock is released together with the transaction.
        await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(hashtext('spark:schema')::bigint)", cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction,
            $"CREATE TABLE IF NOT EXISTS {Table.SchemaHistory} (script text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())",
            cancellationToken).ConfigureAwait(false);

        HashSet<string> applied = await GetAppliedScriptsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        foreach ((string name, string sql) in GetScripts())
        {
            if (applied.Contains(name))
                continue;

            await ExecuteAsync(connection, transaction, sql, cancellationToken).ConfigureAwait(false);

            await using NpgsqlCommand record = new($"INSERT INTO {Table.SchemaHistory} (script) VALUES (@script)", connection, transaction);
            record.Parameters.AddWithValue("script", name);
            await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static IReadOnlyList<(string Name, string Sql)> GetScripts()
    {
        Assembly assembly = typeof(PostgresSchema).Assembly;

        return assembly.GetManifestResourceNames()
            .Where(resource => resource.StartsWith(ScriptPrefix, StringComparison.Ordinal))
            .OrderBy(resource => resource, StringComparer.Ordinal)
            .Select(resource =>
            {
                using Stream stream = assembly.GetManifestResourceStream(resource)
                    ?? throw new InvalidOperationException($"Embedded schema script '{resource}' not found.");
                using StreamReader reader = new(stream);
                return (resource[ScriptPrefix.Length..], reader.ReadToEnd());
            })
            .ToList();
    }

    private static async Task<HashSet<string>> GetAppliedScriptsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        HashSet<string> applied = new(StringComparer.Ordinal);

        await using NpgsqlCommand command = new($"SELECT script FROM {Table.SchemaHistory}", connection, transaction);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            applied.Add(reader.GetString(0));
        }

        return applied;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
