/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;
using Spark.Engine.Core;
using Spark.Engine.Extensions;
using Spark.Engine.Store.Interfaces;
using Spark.Store.PostgreSQL.Extensions;
using Spark.Store.PostgreSQL.Serialization;

namespace Spark.Store.PostgreSQL;

public class PostgresFhirStore : IFhirStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ResourceSerializer _serializer;

    public PostgresFhirStore(NpgsqlDataSource dataSource, IFhirModel fhirModel)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _serializer = new ResourceSerializer(fhirModel);
    }

    public async Task<Entry> AddAsync(Entry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        IKey key = entry.Key;
        key.AssertKeyIsValid();

        string body = EntryRow.SerializeBody(entry, _serializer);
        DateTime updatedAt = EntryRow.GetUpdatedAt(entry);

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync().ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);

        // Look up the surrogate key of the logical resource, creating it when the version id was not
        // allocated by PostgresIdentityGenerator. The no-op update row-locks the resource_keys row,
        // which serializes writers of the same logical resource so that supersede + insert cannot interleave.
        long resourceKey;
        await using (NpgsqlCommand upsertKey = new(
            $"INSERT INTO {Table.ResourceKeys} (type, resource_id, last_version) VALUES (@type, @id, 0) " +
            $"ON CONFLICT (type, resource_id) DO UPDATE SET last_version = {Table.ResourceKeys}.last_version " +
            "RETURNING id",
            connection, transaction))
        {
            upsertKey.Parameters.AddWithValue("type", key.TypeName);
            upsertKey.Parameters.AddWithValue("id", key.ResourceId);
            resourceKey = (long)await upsertKey.ExecuteScalarAsync().ConfigureAwait(false);
        }

        await using (NpgsqlCommand supersede = new(
            $"UPDATE {Table.Resources} SET state = @superseded WHERE resource_key = @resourceKey AND state = @current",
            connection, transaction))
        {
            supersede.Parameters.AddWithValue("superseded", ResourceState.Superseded);
            supersede.Parameters.AddWithValue("current", ResourceState.Current);
            supersede.Parameters.AddWithValue("resourceKey", resourceKey);
            await supersede.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using (NpgsqlCommand insert = new(
            $"INSERT INTO {Table.Resources} (resource_key, type, resource_id, version_id, state, method, updated_at, body) " +
            "VALUES (@resourceKey, @type, @id, @vid, @current, @method, @updatedAt, @body)",
            connection, transaction))
        {
            insert.Parameters.AddWithValue("resourceKey", resourceKey);
            insert.Parameters.AddWithValue("type", key.TypeName);
            insert.Parameters.AddWithValue("id", key.ResourceId);
            insert.Parameters.AddWithValue("vid", key.VersionId);
            insert.Parameters.AddWithValue("current", ResourceState.Current);
            insert.Parameters.AddWithValue("method", entry.Method.ToString());
            insert.Parameters.AddWithValue("updatedAt", updatedAt);
            insert.Parameters.Add(new NpgsqlParameter("body", NpgsqlDbType.Jsonb) { Value = (object)body ?? DBNull.Value });
            await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);

        // Hand back a fresh entry so the caller's instance stays untouched.
        Entry stored = Entry.Create(entry.Method, Key.Create(key.TypeName, key.ResourceId, key.VersionId), new DateTimeOffset(updatedAt));
        if (body != null)
            stored.Resource = _serializer.Deserialize(body);
        return stored;
    }

    public async Task<Entry> GetAsync(IKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        string sql =
            $"SELECT {EntryRow.Columns} FROM {Table.Resources} " +
            $"WHERE resource_key = (SELECT id FROM {Table.ResourceKeys} WHERE type = @type AND resource_id = @id) " +
            (key.HasVersionId() ? "AND version_id = @vid" : "AND state = @current");

        await using NpgsqlCommand command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("type", key.TypeName);
        command.Parameters.AddWithValue("id", key.ResourceId);
        if (key.HasVersionId())
            command.Parameters.AddWithValue("vid", key.VersionId);
        else
            command.Parameters.AddWithValue("current", ResourceState.Current);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        return await reader.ReadAsync().ConfigureAwait(false)
            ? EntryRow.Read(reader, _serializer)
            : null;
    }

    public async Task<IList<Entry>> GetAsync(IEnumerable<IKey> localIdentifiers, IEnumerable<string> elements = null)
    {
        var result = new List<Entry>();

        List<IKey> keys = localIdentifiers?.ToList() ?? [];
        if (keys.Count == 0)
            return result;

        List<IKey> versioned = keys.Where(k => k.HasVersionId()).ToList();
        List<IKey> unversioned = keys.Where(k => !k.HasVersionId()).ToList();
        string[] elementNames = GetElementNames(elements);

        // Current versions and specific versions are looked up in separate branches, so that each can use its own
        // index. Combining them with OR makes PostgreSQL scan the whole resources table.
        const string sql =
            "SELECT found.type, found.resource_id, found.version_id, found.method, found.updated_at, " +
            "  CASE WHEN @elements IS NULL OR found.body IS NULL THEN found.body " +
            "       ELSE (SELECT COALESCE(jsonb_object_agg(e.key, e.value), '{}'::jsonb) " +
            "             FROM jsonb_each(found.body) e WHERE e.key = ANY(@elements)) END " +
            "FROM (" +
            "  SELECT r.* FROM " + Table.ResourceKeys + " k " +
            "  JOIN unnest(@currentTypes, @currentIds) AS c(type, resource_id) ON k.type = c.type AND k.resource_id = c.resource_id " +
            "  JOIN " + Table.Resources + " r ON r.resource_key = k.id AND r.state = @current " +
            "  UNION ALL " +
            "  SELECT r.* FROM " + Table.ResourceKeys + " k " +
            "  JOIN unnest(@versionTypes, @versionIds, @versionVids) AS v(type, resource_id, version_id) ON k.type = v.type AND k.resource_id = v.resource_id " +
            "  JOIN " + Table.Resources + " r ON r.resource_key = k.id AND r.version_id = v.version_id" +
            ") found " +
            "ORDER BY found.id";

        await using NpgsqlCommand command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("current", ResourceState.Current);
        command.Parameters.Add(new NpgsqlParameter("elements", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = (object)elementNames ?? DBNull.Value });
        command.Parameters.AddWithValue("currentTypes", unversioned.Select(k => k.TypeName).ToArray());
        command.Parameters.AddWithValue("currentIds", unversioned.Select(k => k.ResourceId).ToArray());
        command.Parameters.AddWithValue("versionTypes", versioned.Select(k => k.TypeName).ToArray());
        command.Parameters.AddWithValue("versionIds", versioned.Select(k => k.ResourceId).ToArray());
        command.Parameters.AddWithValue("versionVids", versioned.Select(k => k.VersionId).ToArray());

        bool subsetted = elementNames != null;
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            result.Add(EntryRow.Read(reader, _serializer, subsetted: subsetted));
        }

        return result;
    }

    /// <summary>
    /// The JSON members to keep for an _elements request: the requested elements, their primitive
    /// extension siblings, and the members every resource needs to parse.
    /// </summary>
    private static string[] GetElementNames(IEnumerable<string> elements)
    {
        List<string> requested = elements?.ToList() ?? [];
        if (requested.Count == 0)
            return null;

        return ["id", "resourceType", .. requested, .. requested.Select(element => "_" + element)];
    }
}
