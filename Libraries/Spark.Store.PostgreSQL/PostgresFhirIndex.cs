/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Hl7.Fhir.Rest;
using Npgsql;
using Spark.Engine.Core;
using Spark.Engine.Interfaces;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace Spark.Store.PostgreSQL;

/// <summary>
/// Searches the search index tables.
/// </summary>
// TODO: Searching is not implemented yet. Until it is, search requests are answered with 501 Not Implemented.
public class PostgresFhirIndex : IFhirIndex
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresFhirIndex(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task CleanAsync()
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand($"TRUNCATE TABLE {string.Join(", ", Table.SearchIndex)}");
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public Task<SearchResults> SearchAsync(string resource, SearchParams searchCommand) => throw NotImplemented();

    public Task<long> CountAsync(string resource, SearchParams searchCommand) => throw NotImplemented();

    public Task<Key> FindSingleAsync(string resource, SearchParams searchCommand) => throw NotImplemented();

    public Task<SearchResults> GetReverseIncludesAsync(IList<IKey> keys, IList<string> revIncludes) => throw NotImplemented();

    private static SparkException NotImplemented()
    {
        return new SparkException(HttpStatusCode.NotImplemented, "Search is not implemented yet for the PostgreSQL store.");
    }
}
