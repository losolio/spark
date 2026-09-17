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
using Spark.Engine.Interfaces;
using Spark.Engine.Search.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using SearchParameter = Spark.Engine.Model.SearchParameter;
using Task = System.Threading.Tasks.Task;

namespace Spark.Store.PostgreSQL;

/// <summary>
/// Searches the current versions of resources.
/// </summary>
/// <remarks>
/// Parameters that are not defined for the resource type are ignored with a warning, as the MongoDB store
/// does. Parameters that are defined but not implemented yet are answered with 501 Not Implemented rather
/// than ignored, since ignoring them would widen the result, for example making a conditional create match
/// every resource of the type.
/// </remarks>
// TODO: Only searching by type and _id, and sorting by _lastUpdated, is implemented so far.
public class PostgresFhirIndex : IFhirIndex
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IFhirModel _fhirModel;

    public PostgresFhirIndex(NpgsqlDataSource dataSource, IFhirModel fhirModel)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _fhirModel = fhirModel ?? throw new ArgumentNullException(nameof(fhirModel));
    }

    public async Task CleanAsync()
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand($"TRUNCATE TABLE {string.Join(", ", Table.SearchIndex)}");
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<SearchResults> SearchAsync(string resource, SearchParams searchCommand)
    {
        ArgumentException.ThrowIfNullOrEmpty(resource);
        ArgumentNullException.ThrowIfNull(searchCommand);

        SearchResults results = new();
        Query query = CreateQuery(resource, searchCommand, results);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"SELECT r.type, r.resource_id, r.version_id FROM {Table.Resources} r " +
            $"WHERE {query.Filter} ORDER BY {GetOrderBy(searchCommand)}");
        command.Parameters.AddRange(query.Parameters.ToArray());

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            results.Add(Key.Create(reader.GetString(0), reader.GetString(1), reader.GetString(2)).ToOperationPath());
        }

        results.MatchCount = results.Count;
        return results;
    }

    public async Task<long> CountAsync(string resource, SearchParams searchCommand)
    {
        ArgumentException.ThrowIfNullOrEmpty(resource);
        ArgumentNullException.ThrowIfNull(searchCommand);

        Query query = CreateQuery(resource, searchCommand, new SearchResults());

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"SELECT count(*) FROM {Table.Resources} r WHERE {query.Filter}");
        command.Parameters.AddRange(query.Parameters.ToArray());
        return (long)await command.ExecuteScalarAsync().ConfigureAwait(false);
    }

    public async Task<Key> FindSingleAsync(string resource, SearchParams searchCommand)
    {
        SearchResults results = await SearchAsync(resource, searchCommand).ConfigureAwait(false);
        return results.Count switch
        {
            0 => throw Error.BadRequest("No resources were found while searching for a single resource."),
            1 => Key.ParseOperationPath(results[0]),
            _ => throw Error.BadRequest("The search for a single resource yielded more than one."),
        };
    }

    public Task<SearchResults> GetReverseIncludesAsync(IList<IKey> keys, IList<string> revIncludes)
    {
        throw NotImplemented("_revinclude is not implemented yet for the PostgreSQL store.");
    }

    /// <summary>
    /// Builds the filter on the resources table (aliased r) for the criteria of a search, and records the
    /// criteria that are used and the issues with the ones that are not.
    /// </summary>
    private Query CreateQuery(string resourceType, SearchParams searchCommand, SearchResults results)
    {
        Query query = new();
        query.Add("r.type = @type", ("type", resourceType));
        query.Add("r.state = @current", ("current", ResourceState.Current));
        query.Add("r.method <> @delete", ("delete", nameof(Bundle.HTTPVerb.DELETE)));

        foreach ((string name, string value) in searchCommand.Parameters)
        {
            Criterium criterium = Criterium.Parse(_fhirModel.SearchParameters, resourceType, name, value);
            if (criterium == null)
            {
                results.AddIssue(
                    $"Parameter [{(name, value)}] could not be parsed for resource type {resourceType}.",
                    OperationOutcome.IssueSeverity.Warning,
                    OperationOutcome.IssueType.NotSupported);
                continue;
            }

            SearchParameter searchParameter = _fhirModel.FindSearchParameter(resourceType, criterium.ParamName);
            if (searchParameter == null)
            {
                results.AddIssue(
                    $"Parameter with name {criterium.ParamName} is not supported for resource type {resourceType}.",
                    OperationOutcome.IssueSeverity.Warning,
                    OperationOutcome.IssueType.NotSupported);
                continue;
            }

            AddCriterium(query, resourceType, criterium);
            results.UsedCriteria.Add(criterium);
        }

        return query;
    }

    private static void AddCriterium(Query query, string resourceType, Criterium criterium)
    {
        if (criterium.ParamName == "_id" && criterium.Modifier == null && criterium.Operator is Operator.EQ or Operator.IN)
        {
            string[] ids = criterium.Operand is ChoiceValue choice
                ? choice.Choices.Select(id => id.ToUnescapedString()).ToArray()
                : [((ValueExpression)criterium.Operand).ToUnescapedString()];

            string parameter = query.NextParameterName();
            query.Add(
                $"r.resource_key IN (SELECT k.id FROM {Table.ResourceKeys} k WHERE k.type = @type AND k.resource_id = ANY(@{parameter}))",
                (parameter, ids));
            return;
        }

        throw NotImplemented($"Search parameter {criterium} is not implemented yet for the PostgreSQL store.");
    }

    private static string GetOrderBy(SearchParams searchCommand)
    {
        // Newest first unless asked otherwise; the id breaks ties so that paging is stable.
        if (searchCommand.Sort.Count == 0)
            return "r.updated_at DESC, r.id DESC";

        return string.Join(", ", searchCommand.Sort.Select(sort => sort switch
        {
            ("_lastUpdated", SortOrder.Ascending) => "r.updated_at ASC",
            ("_lastUpdated", SortOrder.Descending) => "r.updated_at DESC",
            _ => throw NotImplemented($"Sorting by {sort.Item1} is not implemented yet for the PostgreSQL store."),
        })) + ", r.id ASC";
    }

    private static SparkException NotImplemented(string message)
    {
        return new SparkException(HttpStatusCode.NotImplemented, message);
    }

    private sealed class Query
    {
        private readonly List<string> _clauses = [];

        public List<NpgsqlParameter> Parameters { get; } = [];

        public string Filter => string.Join(" AND ", _clauses);

        public string NextParameterName() => $"p{Parameters.Count}";

        public void Add(string clause, (string Name, object Value) parameter)
        {
            _clauses.Add(clause);
            Parameters.Add(new NpgsqlParameter(parameter.Name, parameter.Value));
        }
    }
}
