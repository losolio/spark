/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Hl7.Fhir.Model;
using Spark.Engine.Core;
using Spark.Engine.Model;
using Spark.Engine.Search.Model;
using Spark.Engine.Search.Types;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Expression = Spark.Engine.Search.Types.Expression;
using SearchParameter = Spark.Engine.Model.SearchParameter;

namespace Spark.Store.PostgreSQL.Search;

/// <summary>
/// Maps the <see cref="IndexValue"/> that <c>IndexService</c> builds for a resource to rows for the
/// search index tables. The type of each search parameter decides the table, and values that do not
/// fit the type of their parameter are skipped.
/// </summary>
internal sealed partial class SearchIndexRowMapper
{
    /// <summary>
    /// Parameters that are answered from the resources table and are not worth a row in the search index:
    /// _id from resource_keys and _lastUpdated from the updated time of the resource.
    /// </summary>
    private static readonly string[] NotIndexed = ["_id", "_lastUpdated"];

    private const string RootName = "root";
    private const string ContainedName = "contained";
    private const string InternalPrefix = "internal_";

    private readonly IFhirModel _fhirModel;
    private readonly HashSet<string> _resourceTypes;

    public SearchIndexRowMapper(IFhirModel fhirModel)
    {
        _fhirModel = fhirModel ?? throw new ArgumentNullException(nameof(fhirModel));
        _resourceTypes = new HashSet<string>(fhirModel.SupportedResources, StringComparer.Ordinal);
    }

    public SearchIndexRows Map(IndexValue root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.Name != RootName)
            throw new ArgumentException("Only a root IndexValue can be mapped.", nameof(root));

        (string resourceType, string resourceId) = GetResource(root);
        SearchIndexRows rows = new(resourceType, resourceId);
        HashSet<string> containedReferences = GetContainedReferences(root);

        foreach (IndexValue parameter in root.IndexValues())
        {
            // TODO: Contained resources are not indexed yet, see 0002_search_index.sql.
            if (parameter.Name == ContainedName || parameter.Name.StartsWith(InternalPrefix, StringComparison.Ordinal))
                continue;
            if (NotIndexed.Contains(parameter.Name, StringComparer.Ordinal))
                continue;

            SearchParameter searchParameter = _fhirModel.FindSearchParameter(resourceType, parameter.Name);
            switch (searchParameter?.Type)
            {
                case SearchParamType.String:
                    MapStrings(parameter, rows);
                    break;
                case SearchParamType.Token:
                    MapTokens(parameter, rows);
                    break;
                case SearchParamType.Number:
                    MapNumbers(parameter, rows);
                    break;
                case SearchParamType.Uri:
                    MapUris(parameter, rows);
                    break;
                case SearchParamType.Date:
                    MapDates(parameter, rows);
                    break;
                case SearchParamType.Quantity:
                    MapQuantities(parameter, rows);
                    break;
                case SearchParamType.Reference:
                    MapReferences(parameter, rows, containedReferences);
                    break;
            }
        }

        return rows;
    }

    /// <summary>
    /// Lower-cases a string and removes its diacritics, so that string searches are case and accent
    /// insensitive. Searches must normalize their values with this method too.
    /// </summary>
    public static string NormalizeString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        string decomposed = value.Normalize(NormalizationForm.FormD);
        StringBuilder builder = new(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    private static (string ResourceType, string ResourceId) GetResource(IndexValue root)
    {
        // IndexService adds internal_id as "<type>/<id>", taken from the key of the resource.
        string reference = GetString(root.IndexValues().FirstOrDefault(value => value.Name == IndexFieldNames.ID));
        int separator = reference?.IndexOf('/') ?? -1;
        if (separator <= 0 || separator == reference.Length - 1)
            throw new ArgumentException($"The root IndexValue has no valid {IndexFieldNames.ID}.", nameof(root));

        return (reference[..separator], reference[(separator + 1)..]);
    }

    /// <summary>
    /// IndexService gives contained resources unique ids and points the references to them at those ids.
    /// Contained resources are not indexed, so references to them must not create resource keys.
    /// </summary>
    private static HashSet<string> GetContainedReferences(IndexValue root)
    {
        return root.IndexValues()
            .Where(value => value.Name == ContainedName)
            .Select(contained => GetString(contained.IndexValues().FirstOrDefault(value => value.Name == IndexFieldNames.ID)))
            .Where(reference => reference != null)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void MapStrings(IndexValue parameter, SearchIndexRows rows)
    {
        foreach (StringValue value in parameter.Values.OfType<StringValue>())
        {
            if (value.Value != null)
                rows.Strings.Add(new StringRow(parameter.Name, NormalizeString(value.Value), value.Value));
        }
    }

    private static void MapTokens(IndexValue parameter, SearchIndexRows rows)
    {
        foreach (Expression value in parameter.Values)
        {
            TokenRow row = value switch
            {
                CompositeValue composite => new TokenRow(
                    parameter.Name,
                    System: GetString(GetComponent(composite, "system")),
                    Code: GetString(GetComponent(composite, "code")),
                    Text: GetString(GetComponent(composite, "text"))),
                // CodeableConcept.text is indexed next to the codings instead of inside one of them.
                IndexValue { Name: "text" } text => new TokenRow(parameter.Name, System: null, Code: null, Text: GetString(text)),
                StringValue code => new TokenRow(parameter.Name, System: null, Code: code.Value, Text: null),
                _ => default,
            };

            if (row.System != null || row.Code != null || row.Text != null)
                rows.Tokens.Add(row);
        }
    }

    private static void MapNumbers(IndexValue parameter, SearchIndexRows rows)
    {
        foreach (NumberValue value in parameter.Values.OfType<NumberValue>())
        {
            rows.Numbers.Add(new NumberRow(parameter.Name, value.Value));
        }
    }

    private static void MapUris(IndexValue parameter, SearchIndexRows rows)
    {
        foreach (StringValue value in parameter.Values.OfType<StringValue>())
        {
            if (value.Value != null)
                rows.Uris.Add(new UriRow(parameter.Name, value.Value));
        }
    }

    /// <summary>
    /// Dates and periods are indexed as the range [start, end) they cover. IndexService has already widened
    /// them to the precision of the value, and a missing start or end leaves the range unbounded on that side.
    /// </summary>
    private static void MapDates(IndexValue parameter, SearchIndexRows rows)
    {
        foreach (CompositeValue period in parameter.Values.OfType<CompositeValue>())
        {
            DateTimeOffset? start = GetDateTime(GetComponent(period, "start"));
            DateTimeOffset? end = GetDateTime(GetComponent(period, "end"));

            // An empty or inverted range can never match a search, and PostgreSQL rejects inverted ones.
            if ((start == null && end == null) || start >= end)
                continue;

            rows.Dates.Add(new DateRow(parameter.Name, start, end));
        }
    }

    /// <summary>
    /// UCUM quantities arrive converted to their canonical unit, other quantities as they were given.
    /// </summary>
    private static void MapQuantities(IndexValue parameter, SearchIndexRows rows)
    {
        foreach (CompositeValue quantity in parameter.Values.OfType<CompositeValue>())
        {
            if (GetComponent(quantity, "value")?.Values.FirstOrDefault() is not NumberValue value)
                continue;

            rows.Quantities.Add(new QuantityRow(
                parameter.Name,
                System: GetString(GetComponent(quantity, "system")),
                Code: GetString(GetComponent(quantity, "unit")),
                Value: value.Value));
        }
    }

    /// <summary>
    /// A reference is a resource on this server (Type/id, optionally with a version), an absolute URL or
    /// URN of a resource elsewhere, or a logical reference by identifier.
    /// </summary>
    private void MapReferences(IndexValue parameter, SearchIndexRows rows, HashSet<string> containedReferences)
    {
        foreach (Expression value in parameter.Values)
        {
            switch (value)
            {
                case StringValue { Value: { } reference }:
                    Match local = LocalReferencePattern().Match(reference);
                    if (local.Success && _resourceTypes.Contains(local.Groups["type"].Value))
                    {
                        string target = $"{local.Groups["type"].Value}/{local.Groups["id"].Value}";
                        // TODO: Contained resources are not indexed yet, see 0002_search_index.sql.
                        if (!containedReferences.Contains(target))
                            rows.References.Add(ReferenceRow.ToResource(parameter.Name, local.Groups["type"].Value, local.Groups["id"].Value));
                    }
                    else if (Uri.TryCreate(reference, UriKind.Absolute, out _))
                    {
                        rows.References.Add(ReferenceRow.ToUrl(parameter.Name, reference));
                    }
                    break;

                case CompositeValue identifier:
                    string system = GetString(GetComponent(identifier, "system"));
                    string code = GetString(GetComponent(identifier, "code"));
                    if (system != null || code != null)
                        rows.References.Add(ReferenceRow.ToIdentifier(parameter.Name, system, code));
                    break;
            }
        }
    }

    [GeneratedRegex(@"^(?<type>[A-Za-z]+)/(?<id>[A-Za-z0-9\-\.]{1,64})(/_history/[A-Za-z0-9\-\.]{1,64})?$")]
    private static partial Regex LocalReferencePattern();

    private static DateTimeOffset? GetDateTime(IndexValue value)
    {
        return value?.Values.FirstOrDefault() is DateTimeValue dateTime ? dateTime.Value : null;
    }

    private static IndexValue GetComponent(CompositeValue composite, string name)
    {
        return composite.Components.OfType<IndexValue>().FirstOrDefault(component => component.Name == name);
    }

    /// <summary>
    /// The string held by an index value. Coded values, such as ContactPoint.system, are themselves
    /// a composite with a code.
    /// </summary>
    private static string GetString(IndexValue value)
    {
        return value?.Values.FirstOrDefault() switch
        {
            StringValue text => text.Value,
            CompositeValue composite => GetString(GetComponent(composite, "code")),
            _ => null,
        };
    }
}
