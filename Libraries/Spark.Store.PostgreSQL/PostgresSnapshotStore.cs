/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Hl7.Fhir.Model;
using Npgsql;
using Spark.Engine.Core;
using Spark.Engine.Store.Interfaces;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Spark.Store.PostgreSQL;

/// <summary>
/// Stores paging snapshots as one row each. PostgreSQL has no practical row size limit for the
/// key array, so the chunking the MongoDB store needs (ADR 0005) does not apply here.
/// </summary>
public class PostgresSnapshotStore : ISnapshotStore2
{
    private const string Columns =
        "id, type, feed_self_link, keys, count, count_param, is_count_only, sort_by, includes, reverse_includes, elements";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresSnapshotStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task AddSnapshotAsync(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"INSERT INTO {Table.Snapshots} ({Columns}, created_at) " +
            "VALUES (@id, @type, @feedSelfLink, @keys, @count, @countParam, @isCountOnly, @sortBy, @includes, @reverseIncludes, @elements, @createdAt)");
        command.Parameters.AddWithValue("id", snapshot.Id);
        command.Parameters.AddWithValue("type", snapshot.Type.ToString());
        command.Parameters.AddWithValue("feedSelfLink", (object)snapshot.FeedSelfLink ?? DBNull.Value);
        command.Parameters.AddWithValue("keys", snapshot.Keys.ToArray());
        command.Parameters.AddWithValue("count", snapshot.Count);
        command.Parameters.AddWithValue("countParam", (object)snapshot.CountParam ?? DBNull.Value);
        command.Parameters.AddWithValue("isCountOnly", snapshot.IsCountOnly);
        command.Parameters.AddWithValue("sortBy", (object)snapshot.SortBy ?? DBNull.Value);
        command.Parameters.AddWithValue("includes", (object)snapshot.Includes?.ToArray() ?? DBNull.Value);
        command.Parameters.AddWithValue("reverseIncludes", (object)snapshot.ReverseIncludes?.ToArray() ?? DBNull.Value);
        command.Parameters.AddWithValue("elements", (object)snapshot.Elements?.ToArray() ?? DBNull.Value);
        command.Parameters.AddWithValue("createdAt", snapshot.WhenCreated.UtcDateTime);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<Snapshot> GetSnapshotAsync(string snapshotId)
    {
        ArgumentNullException.ThrowIfNull(snapshotId);

        await using NpgsqlCommand command = _dataSource.CreateCommand($"SELECT {Columns} FROM {Table.Snapshots} WHERE id = @id");
        command.Parameters.AddWithValue("id", snapshotId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        return await reader.ReadAsync().ConfigureAwait(false) ? ReadSnapshot(reader) : null;
    }

    /// <summary>
    /// Returns the whole snapshot regardless of <paramref name="offset"/>. A single row holds all keys,
    /// so there is no window to select, and the pagination calculator handles the offset itself.
    /// </summary>
    public Task<Snapshot> GetSnapshotAsync(string snapshotId, int offset)
    {
        return GetSnapshotAsync(snapshotId);
    }

    private static Snapshot ReadSnapshot(NpgsqlDataReader reader)
    {
        string id = reader.GetString(0);
        var type = Enum.Parse<Bundle.BundleType>(reader.GetString(1));
        Uri selfLink = new(reader.IsDBNull(2) ? string.Empty : reader.GetString(2), UriKind.RelativeOrAbsolute);
        string[] keys = reader.GetFieldValue<string[]>(3);
        int count = reader.GetInt32(4);
        int? countParam = reader.IsDBNull(5) ? null : reader.GetInt32(5);
        bool isCountOnly = reader.GetBoolean(6);
        string sortBy = reader.IsDBNull(7) ? null : reader.GetString(7);
        string[] includes = reader.IsDBNull(8) ? null : reader.GetFieldValue<string[]>(8);
        string[] reverseIncludes = reader.IsDBNull(9) ? null : reader.GetFieldValue<string[]>(9);
        string[] elements = reader.IsDBNull(10) ? null : reader.GetFieldValue<string[]>(10);

        Snapshot snapshot = isCountOnly
            ? Snapshot.CreateCountOnly(type, selfLink, count)
            : Snapshot.Create(type, selfLink, keys, sortBy, countParam, includes, reverseIncludes, elements);
        snapshot.Id = id;
        return snapshot;
    }
}
