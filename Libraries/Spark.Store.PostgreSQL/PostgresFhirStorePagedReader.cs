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
using System.Threading.Tasks;

namespace Spark.Store.PostgreSQL;

/// <summary>
/// Reads every current resource version page by page, which the index rebuild uses to keep memory
/// bounded.
/// </summary>
public class PostgresFhirStorePagedReader : IFhirStorePagedReader
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ResourceSerializer _serializer;

    public PostgresFhirStorePagedReader(NpgsqlDataSource dataSource, IFhirModel fhirModel)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _serializer = new ResourceSerializer(fhirModel);
    }

    public async Task<IPageResult<Entry>> ReadAsync(FhirStorePageReaderOptions options = null)
    {
        options ??= new FhirStorePageReaderOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.PageSize);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"SELECT count(*) FROM {Table.Resources} WHERE state = @current");
        command.Parameters.AddWithValue("current", ResourceState.Current);
        long totalRecords = (long)await command.ExecuteScalarAsync().ConfigureAwait(false);

        return new PostgresPageResult(_dataSource, _serializer, options.PageSize, totalRecords);
    }
}
