/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Npgsql;
using Spark.Engine.Core;
using Spark.Engine.Extensions;
using Spark.Engine.Store.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Spark.Store.PostgreSQL;

/// <summary>
/// Builds history snapshots from the resources table: every version, newest first, optionally
/// limited to a resource type or a single resource and to versions after <c>_since</c>.
/// </summary>
public class PostgresHistoryStore : IHistoryStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresHistoryStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public Task<Snapshot> HistoryAsync(string typename, HistoryParameters parameters)
    {
        ArgumentException.ThrowIfNullOrEmpty(typename);

        return CreateSnapshotAsync(parameters, "type = @type",
            command => command.Parameters.AddWithValue("type", typename));
    }

    public Task<Snapshot> HistoryAsync(IKey key, HistoryParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(key);

        return CreateSnapshotAsync(parameters,
            $"resource_key = (SELECT id FROM {Table.ResourceKeys} WHERE type = @type AND resource_id = @id)", command =>
        {
            command.Parameters.AddWithValue("type", key.TypeName);
            command.Parameters.AddWithValue("id", key.ResourceId);
        });
    }

    public Task<Snapshot> HistoryAsync(HistoryParameters parameters)
    {
        return CreateSnapshotAsync(parameters, null, null);
    }

    private async Task<Snapshot> CreateSnapshotAsync(HistoryParameters parameters, string filter, Action<NpgsqlCommand> addParameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        List<string> clauses = [];
        if (filter != null)
            clauses.Add(filter);
        if (parameters.Since != null)
            clauses.Add("updated_at > @since");

        string sql = $"SELECT type, resource_id, version_id FROM {Table.Resources}";
        if (clauses.Count > 0)
            sql += " WHERE " + string.Join(" AND ", clauses);
        sql += " ORDER BY updated_at DESC, id DESC";

        await using NpgsqlCommand command = _dataSource.CreateCommand(sql);
        addParameters?.Invoke(command);
        if (parameters.Since != null)
            command.Parameters.AddWithValue("since", parameters.Since.Value.UtcDateTime);

        List<string> keys = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            keys.Add(Key.Create(reader.GetString(0), reader.GetString(1), reader.GetString(2)).ToOperationPath());
        }

        Uri link = new(TransactionBuilder.HISTORY, UriKind.Relative);
        return Snapshot.Create(Bundle.BundleType.History, link, keys, "history", parameters.Count,
            includes: null, reverseIncludes: null, elements: null);
    }
}
