/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Npgsql;
using Spark.Engine.Core;
using Spark.Engine.Store.Interfaces;
using Spark.Store.PostgreSQL.Serialization;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Spark.Store.PostgreSQL;

/// <summary>
/// Pages through current resource versions with keyset pagination on the row id, so rows that are
/// superseded or added while iterating are neither skipped nor returned twice.
/// </summary>
internal sealed class PostgresPageResult : IPageResult<Entry>
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ResourceSerializer _serializer;
    private readonly int _pageSize;

    public PostgresPageResult(NpgsqlDataSource dataSource, ResourceSerializer serializer, int pageSize, long totalRecords)
    {
        _dataSource = dataSource;
        _serializer = serializer;
        _pageSize = pageSize;
        TotalRecords = totalRecords;
    }

    public long TotalRecords { get; }

    public long TotalPages => (long)Math.Ceiling(TotalRecords / (double)_pageSize);

    public async Task IterateAllPagesAsync(Func<IReadOnlyList<Entry>, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        long lastId = 0;
        while (true)
        {
            List<Entry> page = new(_pageSize);

            await using (NpgsqlCommand command = _dataSource.CreateCommand(
                $"SELECT id, {EntryRow.Columns} FROM {Table.Resources} " +
                "WHERE state = @current AND id > @lastId ORDER BY id LIMIT @pageSize"))
            {
                command.Parameters.AddWithValue("current", ResourceState.Current);
                command.Parameters.AddWithValue("lastId", lastId);
                command.Parameters.AddWithValue("pageSize", _pageSize);

                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    lastId = reader.GetInt64(0);
                    page.Add(EntryRow.Read(reader, _serializer, offset: 1));
                }
            }

            if (page.Count == 0)
                return;

            await callback(page).ConfigureAwait(false);

            if (page.Count < _pageSize)
                return;
        }
    }
}
