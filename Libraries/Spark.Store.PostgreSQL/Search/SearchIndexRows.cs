/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using System.Collections.Generic;

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
}

internal readonly record struct StringRow(string Param, string ValueNormalized, string ValueExact);

internal readonly record struct TokenRow(string Param, string System, string Code, string Text);

internal readonly record struct NumberRow(string Param, decimal Value);

internal readonly record struct UriRow(string Param, string Value);
