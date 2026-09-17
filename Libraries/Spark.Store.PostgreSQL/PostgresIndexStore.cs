/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Npgsql;
using Spark.Engine.Core;
using Spark.Engine.Model;
using Spark.Engine.Store.Interfaces;
using Spark.Store.PostgreSQL.Search;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Spark.Store.PostgreSQL;

/// <summary>
/// Writes the search index of a resource to the search index tables. Saving a resource replaces all of
/// its rows, so the index always reflects exactly the last indexed version.
/// </summary>
/// <remarks>
/// Search parameter ids are cached for the lifetime of the instance, so the store should be registered
/// as a singleton. <see cref="CleanAsync"/> keeps the search_params table, so cached ids stay valid.
/// </remarks>
public class PostgresIndexStore : IIndexStore
{
    private static readonly string[] SearchTables =
    [
        Table.SearchString, Table.SearchToken, Table.SearchDate, Table.SearchNumber,
        Table.SearchQuantity, Table.SearchReference, Table.SearchUri,
    ];

    private readonly NpgsqlDataSource _dataSource;
    private readonly SearchIndexRowMapper _mapper;
    private readonly ConcurrentDictionary<(string ResourceType, string Code), short> _paramIds = new();

    public PostgresIndexStore(NpgsqlDataSource dataSource, IFhirModel fhirModel)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _mapper = new SearchIndexRowMapper(fhirModel);
    }

    public async Task SaveAsync(IndexValue indexValue)
    {
        ArgumentNullException.ThrowIfNull(indexValue);

        SearchIndexRows rows = _mapper.Map(indexValue);
        IReadOnlyDictionary<string, short> paramIds = await GetParamIdsAsync(rows).ConfigureAwait(false);

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync().ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);

        // The no-op update row-locks the resource_keys row, which serializes indexing of the same
        // resource so that two saves cannot interleave their deletes and inserts.
        long resourceKey;
        await using (NpgsqlCommand upsertKey = new(
            $"INSERT INTO {Table.ResourceKeys} (type, resource_id, last_version) VALUES (@type, @id, 0) " +
            $"ON CONFLICT (type, resource_id) DO UPDATE SET last_version = {Table.ResourceKeys}.last_version " +
            "RETURNING id",
            connection, transaction))
        {
            upsertKey.Parameters.AddWithValue("type", rows.ResourceType);
            upsertKey.Parameters.AddWithValue("id", rows.ResourceId);
            resourceKey = (long)await upsertKey.ExecuteScalarAsync().ConfigureAwait(false);
        }

        await using (NpgsqlBatch batch = new(connection, transaction))
        {
            AddDeletes(batch, resourceKey);

            if (rows.Strings.Count > 0)
            {
                AddInsert(batch, resourceKey,
                    $"INSERT INTO {Table.SearchString} (resource_key, param_id, value_normalized, value_exact) " +
                    "SELECT @resourceKey, * FROM unnest(@param, @normalized, @exact)",
                    ("param", rows.Strings.Select(row => paramIds[row.Param]).ToArray()),
                    ("normalized", rows.Strings.Select(row => row.ValueNormalized).ToArray()),
                    ("exact", rows.Strings.Select(row => row.ValueExact).ToArray()));
            }

            if (rows.Tokens.Count > 0)
            {
                AddInsert(batch, resourceKey,
                    $"INSERT INTO {Table.SearchToken} (resource_key, param_id, system, code, text) " +
                    "SELECT @resourceKey, * FROM unnest(@param, @system, @code, @text)",
                    ("param", rows.Tokens.Select(row => paramIds[row.Param]).ToArray()),
                    ("system", rows.Tokens.Select(row => row.System).ToArray()),
                    ("code", rows.Tokens.Select(row => row.Code).ToArray()),
                    ("text", rows.Tokens.Select(row => row.Text).ToArray()));
            }

            if (rows.Numbers.Count > 0)
            {
                AddInsert(batch, resourceKey,
                    $"INSERT INTO {Table.SearchNumber} (resource_key, param_id, value) " +
                    "SELECT @resourceKey, * FROM unnest(@param, @value)",
                    ("param", rows.Numbers.Select(row => paramIds[row.Param]).ToArray()),
                    ("value", rows.Numbers.Select(row => row.Value).ToArray()));
            }

            if (rows.Uris.Count > 0)
            {
                AddInsert(batch, resourceKey,
                    $"INSERT INTO {Table.SearchUri} (resource_key, param_id, value) " +
                    "SELECT @resourceKey, * FROM unnest(@param, @value)",
                    ("param", rows.Uris.Select(row => paramIds[row.Param]).ToArray()),
                    ("value", rows.Uris.Select(row => row.Value).ToArray()));
            }

            await batch.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
    }

    public async Task DeleteAsync(Entry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync().ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);

        object resourceKey;
        await using (NpgsqlCommand lockKey = new(
            $"SELECT id FROM {Table.ResourceKeys} WHERE type = @type AND resource_id = @id FOR UPDATE",
            connection, transaction))
        {
            lockKey.Parameters.AddWithValue("type", entry.Key.TypeName);
            lockKey.Parameters.AddWithValue("id", entry.Key.ResourceId);
            resourceKey = await lockKey.ExecuteScalarAsync().ConfigureAwait(false);
        }

        // A resource that was never stored has nothing indexed.
        if (resourceKey is long key)
        {
            await using NpgsqlBatch batch = new(connection, transaction);
            AddDeletes(batch, key);
            await batch.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
    }

    public async Task CleanAsync()
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand($"TRUNCATE TABLE {string.Join(", ", SearchTables)}");
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Looks up the ids of the search parameters used by the rows, creating the missing ones.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, short>> GetParamIdsAsync(SearchIndexRows rows)
    {
        string[] codes = rows.Strings.Select(row => row.Param)
            .Concat(rows.Tokens.Select(row => row.Param))
            .Concat(rows.Numbers.Select(row => row.Param))
            .Concat(rows.Uris.Select(row => row.Param))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Dictionary<string, short> ids = new(StringComparer.Ordinal);
        List<string> missing = [];
        foreach (string code in codes)
        {
            if (_paramIds.TryGetValue((rows.ResourceType, code), out short id))
                ids[code] = id;
            else
                missing.Add(code);
        }

        if (missing.Count == 0)
            return ids;

        // Only codes that do not exist are inserted: ON CONFLICT DO NOTHING still draws a value from the
        // identity sequence, and a smallint sequence cannot afford one per lookup. Codes are inserted in
        // order so that concurrent inserts of the same codes cannot deadlock.
        await using NpgsqlBatch batch = _dataSource.CreateBatch();
        batch.BatchCommands.Add(CreateBatchCommand(
            $"INSERT INTO {Table.SearchParams} (resource_type, code) " +
            "SELECT @type, c.code FROM unnest(@codes) AS c(code) " +
            $"WHERE NOT EXISTS (SELECT 1 FROM {Table.SearchParams} p WHERE p.resource_type = @type AND p.code = c.code) " +
            "ORDER BY c.code " +
            "ON CONFLICT (resource_type, code) DO NOTHING",
            ("type", rows.ResourceType), ("codes", missing.ToArray())));
        batch.BatchCommands.Add(CreateBatchCommand(
            $"SELECT code, id FROM {Table.SearchParams} WHERE resource_type = @type AND code = ANY(@codes)",
            ("type", rows.ResourceType), ("codes", missing.ToArray())));

        await using NpgsqlDataReader reader = await batch.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            string code = reader.GetString(0);
            short id = reader.GetInt16(1);
            _paramIds[(rows.ResourceType, code)] = id;
            ids[code] = id;
        }

        return ids;
    }

    private static void AddDeletes(NpgsqlBatch batch, long resourceKey)
    {
        foreach (string table in SearchTables)
        {
            batch.BatchCommands.Add(CreateBatchCommand(
                $"DELETE FROM {table} WHERE resource_key = @resourceKey",
                ("resourceKey", resourceKey)));
        }
    }

    private static void AddInsert(NpgsqlBatch batch, long resourceKey, string sql, params (string Name, object Value)[] columns)
    {
        batch.BatchCommands.Add(CreateBatchCommand(sql, [("resourceKey", resourceKey), .. columns]));
    }

    private static NpgsqlBatchCommand CreateBatchCommand(string sql, params (string Name, object Value)[] parameters)
    {
        NpgsqlBatchCommand command = new(sql);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }
}
