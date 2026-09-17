/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Utility;
using Npgsql;
using Spark.Engine.Core;
using Spark.Engine.Extensions;
using Spark.Engine.Interfaces;
using Spark.Engine.Search;
using Spark.Engine.Search.Support;
using Spark.Engine.Search.Types;
using Spark.Store.PostgreSQL.Search;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Error = Spark.Engine.Core.Error;
using Metrics = Fhir.Metrics;
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
// TODO: Only searching by type, _id, _lastUpdated and token, string, reference, quantity and date parameters,
//       and sorting by _lastUpdated, is implemented so far.
public partial class PostgresFhirIndex : IFhirIndex
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IFhirModel _fhirModel;
    private readonly IReferenceNormalizationService _referenceNormalizationService;
    private readonly HashSet<string> _resourceTypes;

    public PostgresFhirIndex(
        NpgsqlDataSource dataSource,
        IFhirModel fhirModel,
        IReferenceNormalizationService referenceNormalizationService = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _fhirModel = fhirModel ?? throw new ArgumentNullException(nameof(fhirModel));
        _referenceNormalizationService = referenceNormalizationService;
        _resourceTypes = new HashSet<string>(fhirModel.SupportedResources, StringComparer.Ordinal);
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
        Scope scope = new("r", query.AddParameter(resourceType), resourceType);
        List<string> clauses = [GetResourceFilter(query, scope)];

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

            if (!IsDefined(resourceType, criterium))
            {
                results.AddIssue(
                    $"Parameter with name {criterium.ParamName} is not supported for resource type {resourceType}.",
                    OperationOutcome.IssueSeverity.Warning,
                    OperationOutcome.IssueType.NotSupported);
                continue;
            }

            clauses.Add(GetCriteriumClause(query, scope, criterium));
            results.UsedCriteria.Add(criterium);
        }

        query.Filter = string.Join(" AND ", clauses);
        return query;
    }

    /// <summary>
    /// Whether the parameter is defined for the resource type and, for a chain, whether every link of the chain
    /// is defined for at least one of the resource types it can point to.
    /// </summary>
    private bool IsDefined(string resourceType, Criterium criterium)
    {
        SearchParameter searchParameter = _fhirModel.FindSearchParameter(resourceType, criterium.ParamName);
        if (searchParameter == null)
            return false;
        if (criterium.Operator != Operator.CHAIN)
            return true;

        Criterium next = (Criterium)criterium.Operand;
        return GetTargetTypes(criterium, searchParameter).Any(target => IsDefined(target, next));
    }

    /// <summary>The current, not deleted versions of resources of the type of the scope.</summary>
    private static string GetResourceFilter(Query query, Scope scope)
    {
        return $"{scope.Alias}.type = @{scope.TypeParameter} " +
               $"AND {scope.Alias}.state = @{query.Constant("current", ResourceState.Current)} " +
               $"AND {scope.Alias}.method <> @{query.Constant("delete", nameof(Bundle.HTTPVerb.DELETE))}";
    }

    private string GetCriteriumClause(Query query, Scope scope, Criterium criterium)
    {
        SearchParameter searchParameter = _fhirModel.FindSearchParameter(scope.ResourceType, criterium.ParamName);

        switch (criterium.ParamName)
        {
            case "_id" when criterium.Modifier == null && criterium.Operator is Operator.EQ or Operator.IN:
                string[] ids = GetValues(criterium).Select(id => id.ToUnescapedString()).ToArray();
                return $"{scope.Alias}.resource_key IN (SELECT k.id FROM {Table.ResourceKeys} k " +
                       $"WHERE k.type = @{scope.TypeParameter} AND k.resource_id = ANY(@{query.AddParameter(ids)}))";

            case "_lastUpdated" when criterium.Modifier == null:
                return GetLastUpdatedClause(query, scope, criterium);
        }

        if (criterium.Operator == Operator.CHAIN && searchParameter.Type == SearchParamType.Reference)
            return GetChainClause(query, scope, criterium, searchParameter);

        if (criterium.Operator != Operator.CHAIN)
        {
            switch (searchParameter.Type)
            {
                case SearchParamType.Token when criterium.Modifier is null or "not":
                    return GetIndexClause(query, scope, criterium, Table.SearchToken,
                        value => GetTokenCondition(query, value, "i.system", "i.code"));

                case SearchParamType.String when criterium.Modifier is null or "exact" or "contains":
                    return GetIndexClause(query, scope, criterium, Table.SearchString,
                        value => GetStringCondition(query, criterium.Modifier, value));

                case SearchParamType.Reference when criterium.Modifier is null or "identifier" || _resourceTypes.Contains(criterium.Modifier):
                    return GetIndexClause(query, scope, criterium, Table.SearchReference,
                        value => GetReferenceCondition(query, criterium, searchParameter, value));

                case SearchParamType.Quantity when criterium.Modifier is null:
                    return GetIndexClause(query, scope, criterium, Table.SearchQuantity,
                        (comparator, value) => GetQuantityCondition(query, comparator, value));

                case SearchParamType.Date when criterium.Modifier is null:
                    return GetIndexClause(query, scope, criterium, Table.SearchDate,
                        (comparator, value) => GetDateCondition(query, comparator, value));
            }
        }

        throw NotImplemented($"Search parameter {criterium} is not implemented yet for the PostgreSQL store.");
    }

    /// <summary>
    /// The last updated time of a resource is an instant, while a search value covers a range that depends on
    /// its precision: 2026-09 is all of September. The range is compared as [lower bound, upper bound).
    /// </summary>
    private static string GetLastUpdatedClause(Query query, Scope scope, Criterium criterium)
    {
        return criterium.Operator switch
        {
            // Every resource has a last updated time.
            Operator.ISNULL => "FALSE",
            Operator.NOTNULL => "TRUE",
            Operator.IN => "(" + string.Join(" OR ", GetValues(criterium)
                .Select(value => GetLastUpdatedClause(query, scope, Operator.EQ, value))) + ")",
            _ => GetLastUpdatedClause(query, scope, criterium.Operator, (ValueExpression)criterium.Operand),
        };
    }

    private static string GetLastUpdatedClause(Query query, Scope scope, Operator comparator, ValueExpression operand)
    {
        string text = operand.ToUnescapedString();
        if (!FhirDateTime.IsValidValue(text))
            throw Error.BadRequest($"'{text}' is not a valid value for _lastUpdated.");

        FhirDateTime value = new(text);
        string lower = query.AddParameter(value.LowerBound().UtcDateTime);
        string upper = query.AddParameter(value.UpperBound().UtcDateTime);
        string column = $"{scope.Alias}.updated_at";

        return comparator switch
        {
            // Like the MongoDB store, ap is treated as eq.
            Operator.EQ or Operator.APPROX => $"({column} >= @{lower} AND {column} < @{upper})",
            Operator.NOT_EQUAL => $"({column} < @{lower} OR {column} >= @{upper})",
            Operator.GT or Operator.STARTS_AFTER => $"{column} >= @{upper}",
            Operator.GTE => $"{column} >= @{lower}",
            Operator.LT or Operator.ENDS_BEFORE => $"{column} < @{lower}",
            Operator.LTE => $"{column} < @{upper}",
            _ => throw NotImplemented($"The {comparator} comparator is not implemented for _lastUpdated."),
        };
    }

    /// <summary>
    /// A criterium that matches resources by their rows in a search index table (aliased i). A resource matches
    /// when any of its rows for the parameter matches any of the values.
    /// </summary>
    private static string GetIndexClause(Query query, Scope scope, Criterium criterium, string table, Func<string, string> getCondition)
    {
        return GetIndexClause(query, scope, criterium, table, (comparator, value) => comparator == Operator.EQ
            ? getCondition(value)
            : throw NotImplemented($"The {comparator} comparator is not implemented for {criterium.ParamName}."));
    }

    private static string GetIndexClause(Query query, Scope scope, Criterium criterium, string table, Func<Operator, string, string> getCondition)
    {
        string rows =
            $"SELECT 1 FROM {table} i WHERE i.resource_key = {scope.Alias}.resource_key AND i.param_id = " +
            $"(SELECT p.id FROM {Table.SearchParams} p WHERE p.resource_type = @{scope.TypeParameter} " +
            $"AND p.code = @{query.AddParameter(criterium.ParamName)})";

        return criterium.Operator switch
        {
            Operator.ISNULL => $"NOT EXISTS ({rows})",
            Operator.NOTNULL => $"EXISTS ({rows})",
            _ when criterium.Modifier == "not" =>
                // :not matches resources that have no matching value, including resources without the parameter.
                $"NOT EXISTS ({rows} AND ({GetMatches(criterium, getCondition)}))",
            _ => $"EXISTS ({rows} AND ({GetMatches(criterium, getCondition)}))",
        };
    }

    private static string GetMatches(Criterium criterium, Func<Operator, string, string> getCondition)
    {
        // Several values are only allowed without a comparator, and each of them is then an equality.
        Operator comparator = criterium.Operator == Operator.IN ? Operator.EQ : criterium.Operator;
        return string.Join(" OR ", GetValues(criterium).Select(value => $"({getCondition(comparator, GetEscapedValue(criterium, value))})"));
    }

    /// <summary>
    /// Both the search value and the indexed value are ranges: 2026-09 is all of September, and a period without
    /// an end is unbounded. The comparators follow the FHIR definitions for ranges, where eq means that the
    /// search range contains the indexed range, and gt means that the indexed range reaches beyond the search range.
    /// </summary>
    private static string GetDateCondition(Query query, Operator comparator, string value)
    {
        string text = StringValue.UnescapeString(value);
        if (!FhirDateTime.IsValidValue(text))
            throw Error.BadRequest($"'{text}' is not a valid date.");

        FhirDateTime date = new(text);
        string lower = query.AddParameter(date.LowerBound().UtcDateTime);
        string upper = query.AddParameter(date.UpperBound().UtcDateTime);
        string search = $"tstzrange(@{lower}, @{upper}, '[)')";
        string contained = $"i.period <@ {search}";
        string above = $"i.period && tstzrange(@{upper}, NULL, '[)')";
        string below = $"i.period && tstzrange(NULL, @{lower}, '[)')";

        return comparator switch
        {
            Operator.EQ => contained,
            Operator.NOT_EQUAL => $"NOT ({contained})",
            Operator.GT => above,
            Operator.LT => below,
            Operator.GTE => $"{above} OR {contained}",
            Operator.LTE => $"{below} OR {contained}",
            Operator.STARTS_AFTER => $"i.period >> {search}",
            Operator.ENDS_BEFORE => $"i.period << {search}",
            Operator.APPROX => $"i.period && {search}",
            _ => throw NotImplemented($"The {comparator} comparator is not implemented for dates."),
        };
    }

    /// <summary>
    /// A quantity is number, number||code (any system) or number|system|code. The number has an implicit
    /// precision, so 5.4 equals any value in [5.35, 5.45), while gt, ge, lt and le compare with the number itself.
    /// UCUM quantities are converted to their canonical unit first, as they are when they are indexed.
    /// </summary>
    private static string GetQuantityCondition(Query query, Operator comparator, string value)
    {
        string[] parts = value.SplitNotEscaped('|');
        if (parts.Length is not (1 or 3) || !decimal.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number))
            throw Error.BadRequest($"'{value}' is not a valid quantity.");

        string system = parts.Length == 3 ? NullIfEmpty(StringValue.UnescapeString(parts[1])) : null;
        string code = parts.Length == 3 ? NullIfEmpty(StringValue.UnescapeString(parts[2])) : null;

        // The implicit range of the number, widened by 10% for ap.
        decimal half = 0.5m / Pow10(number.Scale);
        decimal margin = comparator == Operator.APPROX ? Math.Max(half, Math.Abs(number) * 0.1m) : half;
        (decimal Number, decimal Lower, decimal Upper) range = (number, number - margin, number + margin);

        List<string> units = [];
        if (code != null && system is null or QuantityExtensions.UcumUriString && TryCanonicalize(range, code, out var canonical))
        {
            units.Add($"i.system = @{query.AddParameter(QuantityExtensions.UcumUriString)} AND i.code = @{query.AddParameter(canonical.Code)} " +
                      $"AND {GetNumberComparison(query, comparator, canonical.Range)}");
        }

        if (code == null || system != QuantityExtensions.UcumUriString)
        {
            string unit = code == null ? "TRUE" : $"i.code = @{query.AddParameter(code)}";
            if (system != null)
                unit += $" AND i.system = @{query.AddParameter(system)}";
            units.Add($"{unit} AND {GetNumberComparison(query, comparator, range)}");
        }

        return units.Count == 0 ? "FALSE" : string.Join(" OR ", units.Select(unit => $"({unit})"));
    }

    private static string GetNumberComparison(Query query, Operator comparator, (decimal Number, decimal Lower, decimal Upper) range)
    {
        return comparator switch
        {
            Operator.EQ or Operator.APPROX => $"i.value >= @{query.AddParameter(range.Lower)} AND i.value < @{query.AddParameter(range.Upper)}",
            Operator.NOT_EQUAL => $"(i.value < @{query.AddParameter(range.Lower)} OR i.value >= @{query.AddParameter(range.Upper)})",
            Operator.GT or Operator.STARTS_AFTER => $"i.value > @{query.AddParameter(range.Number)}",
            Operator.GTE => $"i.value >= @{query.AddParameter(range.Number)}",
            Operator.LT or Operator.ENDS_BEFORE => $"i.value < @{query.AddParameter(range.Number)}",
            Operator.LTE => $"i.value <= @{query.AddParameter(range.Number)}",
            _ => throw NotImplemented($"The {comparator} comparator is not implemented for quantities."),
        };
    }

    /// <summary>
    /// Converts the number and its range to the canonical UCUM unit. Returns false when the code is not a UCUM unit.
    /// </summary>
    private static bool TryCanonicalize(
        (decimal Number, decimal Lower, decimal Upper) range,
        string code,
        out (string Code, (decimal Number, decimal Lower, decimal Upper) Range) canonical)
    {
        canonical = default;
        try
        {
            Metrics.Quantity number = Canonicalize(range.Number, code);
            canonical = (number.Metric.ToString(), ((decimal)number.Value, (decimal)Canonicalize(range.Lower, code).Value, (decimal)Canonicalize(range.Upper, code).Value));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static Metrics.Quantity Canonicalize(decimal value, string code)
    {
        return new Quantity { Value = value, System = QuantityExtensions.UcumUriString, Code = code }
            .ToUnitsOfMeasureQuantity()
            .Canonical();
    }

    private static decimal Pow10(int exponent)
    {
        decimal result = 1m;
        for (int i = 0; i < exponent; i++)
            result *= 10m;
        return result;
    }

    private static string NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// A token is code, system|code, |code (a code without a system) or system| (any code in the system).
    /// </summary>
    private static string GetTokenCondition(Query query, string value, string systemColumn, string codeColumn)
    {
        string[] parts = value.SplitNotEscaped('|');
        if (parts.Length > 2)
            throw Error.BadRequest($"'{value}' is not a valid token.");

        string code = StringValue.UnescapeString(parts[^1]);
        if (parts.Length == 1)
            return $"{codeColumn} = @{query.AddParameter(code)}";

        string system = StringValue.UnescapeString(parts[0]);
        string systemCondition = system.Length == 0
            ? $"{systemColumn} IS NULL"
            : $"{systemColumn} = @{query.AddParameter(system)}";

        return code.Length == 0
            ? systemCondition
            : $"{systemCondition} AND {codeColumn} = @{query.AddParameter(code)}";
    }

    /// <summary>
    /// Strings match case and accent insensitively on the start of the value, anywhere in the value with
    /// :contains, or exactly with :exact.
    /// </summary>
    private static string GetStringCondition(Query query, string modifier, string value)
    {
        string text = StringValue.UnescapeString(value);
        string normalized = SearchIndexRowMapper.NormalizeString(text);

        return modifier switch
        {
            // The normalized value narrows the search down using the index before the exact comparison.
            "exact" => $"i.value_normalized = @{query.AddParameter(normalized)} AND i.value_exact = @{query.AddParameter(text)}",
            "contains" => $"i.value_normalized LIKE @{query.AddParameter("%" + EscapeLikePattern(normalized) + "%")}",
            _ => $"i.value_normalized LIKE @{query.AddParameter(EscapeLikePattern(normalized) + "%")}",
        };
    }

    /// <summary>
    /// A reference is searched by id (123, with the type from the modifier or the targets of the parameter),
    /// by type and id (Patient/123, or a URL of this server), by a URL of another server, or by identifier with
    /// the :identifier modifier.
    /// </summary>
    private string GetReferenceCondition(Query query, Criterium criterium, SearchParameter searchParameter, string value)
    {
        if (criterium.Modifier == "identifier")
            return GetTokenCondition(query, value, "i.identifier_system", "i.identifier_value");

        string reference = StringValue.UnescapeString(value);
        if (_referenceNormalizationService?.GetNormalizedReferenceValue(new UntypedValue(reference), null) is { } normalized)
            reference = normalized.ToUnescapedString();

        Match local = LocalReferencePattern().Match(reference);
        if (local.Success && _resourceTypes.Contains(local.Groups["type"].Value))
        {
            string type = local.Groups["type"].Value;
            // A type modifier that contradicts the type in the value can never match.
            if (criterium.Modifier != null && criterium.Modifier != type)
                return "FALSE";

            return $"i.target_key = (SELECT k.id FROM {Table.ResourceKeys} k " +
                   $"WHERE k.type = @{query.AddParameter(type)} AND k.resource_id = @{query.AddParameter(local.Groups["id"].Value)})";
        }

        if (IdPattern().IsMatch(reference))
        {
            string[] types = GetTargetTypes(criterium, searchParameter).ToArray();
            return $"i.target_key IN (SELECT k.id FROM {Table.ResourceKeys} k " +
                   $"WHERE k.resource_id = @{query.AddParameter(reference)} AND k.type = ANY(@{query.AddParameter(types)}))";
        }

        return Uri.TryCreate(reference, UriKind.Absolute, out _)
            ? $"i.target_url = @{query.AddParameter(reference)}"
            : "FALSE";
    }

    /// <summary>
    /// A chain such as subject:Patient.name=x matches resources whose reference points to a resource of one of
    /// the target types that matches the rest of the chain.
    /// </summary>
    private string GetChainClause(Query query, Scope scope, Criterium criterium, SearchParameter searchParameter)
    {
        Criterium next = (Criterium)criterium.Operand;
        List<string> targets = [];

        foreach (string targetType in GetTargetTypes(criterium, searchParameter))
        {
            if (!IsDefined(targetType, next))
                continue;

            Scope target = new(query.NextAlias(), query.AddParameter(targetType), targetType);
            targets.Add(
                $"SELECT {target.Alias}.resource_key FROM {Table.Resources} {target.Alias} " +
                $"WHERE {GetResourceFilter(query, target)} AND {GetCriteriumClause(query, target, next)}");
        }

        string rows =
            $"SELECT 1 FROM {Table.SearchReference} i WHERE i.resource_key = {scope.Alias}.resource_key AND i.param_id = " +
            $"(SELECT p.id FROM {Table.SearchParams} p WHERE p.resource_type = @{scope.TypeParameter} " +
            $"AND p.code = @{query.AddParameter(criterium.ParamName)})";

        return targets.Count == 0
            ? "FALSE"
            : $"EXISTS ({rows} AND i.target_key IN ({string.Join(" UNION ALL ", targets)}))";
    }

    /// <summary>
    /// The resource types a reference parameter can point to: the type given as modifier, or else the targets
    /// of the parameter.
    /// </summary>
    private IEnumerable<string> GetTargetTypes(Criterium criterium, SearchParameter searchParameter)
    {
        if (criterium.Modifier != null && _resourceTypes.Contains(criterium.Modifier))
            return [criterium.Modifier];

        return searchParameter.Target
            .Select(target => target.GetLiteral())
            .Where(_resourceTypes.Contains);
    }

    private static ValueExpression[] GetValues(Criterium criterium)
    {
        return criterium.Operand is ChoiceValue choice
            ? choice.Choices
            : [(ValueExpression)criterium.Operand];
    }

    private static string EscapeLikePattern(string value)
    {
        return value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
    }

    /// <summary>
    /// The value as it was given, still escaped, so that separators such as | can be told apart from
    /// escaped ones.
    /// </summary>
    private static string GetEscapedValue(Criterium criterium, ValueExpression value)
    {
        return value is UntypedValue untyped
            ? untyped.Value
            : throw Error.BadRequest($"'{value}' is not a valid value for {criterium.ParamName}.");
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

    [GeneratedRegex(@"^(?<type>[A-Za-z]+)/(?<id>[A-Za-z0-9\-\.]{1,64})(/_history/[A-Za-z0-9\-\.]{1,64})?$")]
    private static partial Regex LocalReferencePattern();

    [GeneratedRegex(@"^[A-Za-z0-9\-\.]{1,64}$")]
    private static partial Regex IdPattern();

    /// <summary>
    /// The resources table a criterium applies to: its alias, the parameter holding its resource type, and the
    /// resource type itself.
    /// </summary>
    private readonly record struct Scope(string Alias, string TypeParameter, string ResourceType);

    private sealed class Query
    {
        private readonly Dictionary<string, string> _constants = [];
        private int _aliases;

        public List<NpgsqlParameter> Parameters { get; } = [];

        public string Filter { get; set; }

        /// <summary>Adds a parameter with a generated name and returns the name.</summary>
        public string AddParameter(object value)
        {
            string name = $"p{Parameters.Count}";
            Parameters.Add(new NpgsqlParameter(name, value));
            return name;
        }

        /// <summary>Adds a parameter with a fixed name the first time it is used and returns the name.</summary>
        public string Constant(string name, object value)
        {
            if (_constants.TryAdd(name, name))
                Parameters.Add(new NpgsqlParameter(name, value));
            return name;
        }

        /// <summary>A new alias for a resources table in a subquery.</summary>
        public string NextAlias() => $"r{++_aliases}";
    }
}
