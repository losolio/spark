/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace Spark.Store.PostgreSQL.Search;

/// <summary>
/// The search index rows of one resource, grouped by search index table. Rows refer to their search
/// parameter by code; the index store resolves the codes and the resource to their ids.
/// </summary>
internal sealed class SearchIndexRows
{
    public SearchIndexRows(string resourceType, string resourceId)
    {
        ResourceType = resourceType;
        ResourceId = resourceId;
    }

    public string ResourceType { get; }

    public string ResourceId { get; }

    public List<StringRow> Strings { get; } = [];

    public List<TokenRow> Tokens { get; } = [];

    public List<NumberRow> Numbers { get; } = [];

    public List<UriRow> Uris { get; } = [];

    public List<DateRow> Dates { get; } = [];

    public List<QuantityRow> Quantities { get; } = [];

    public List<ReferenceRow> References { get; } = [];

    /// <summary>The distinct search parameter codes used by the rows.</summary>
    public IEnumerable<string> Params =>
        Strings.Select(row => row.Param)
            .Concat(Tokens.Select(row => row.Param))
            .Concat(Numbers.Select(row => row.Param))
            .Concat(Uris.Select(row => row.Param))
            .Concat(Dates.Select(row => row.Param))
            .Concat(Quantities.Select(row => row.Param))
            .Concat(References.Select(row => row.Param))
            .Distinct(StringComparer.Ordinal);
}

internal readonly record struct StringRow(string Param, string ValueNormalized, string ValueExact);

internal readonly record struct TokenRow(string Param, string System, string Code, string Text);

internal readonly record struct NumberRow(string Param, decimal Value);

internal readonly record struct UriRow(string Param, string Value);

/// <summary>The range [Start, End) a date covers; a null start or end is unbounded.</summary>
internal readonly record struct DateRow(string Param, DateTimeOffset? Start, DateTimeOffset? End);

internal readonly record struct QuantityRow(string Param, string System, string Code, decimal Value);

/// <summary>
/// A reference to a resource on this server (TargetType and TargetId), to a URL elsewhere (TargetUrl), or by
/// identifier (IdentifierSystem and IdentifierValue).
/// </summary>
internal readonly record struct ReferenceRow(
    string Param,
    string TargetType,
    string TargetId,
    string TargetUrl,
    string IdentifierSystem,
    string IdentifierValue)
{
    public static ReferenceRow ToResource(string param, string type, string id) => new(param, type, id, null, null, null);

    public static ReferenceRow ToUrl(string param, string url) => new(param, null, null, url, null, null);

    public static ReferenceRow ToIdentifier(string param, string system, string value) => new(param, null, null, null, system, value);
}
