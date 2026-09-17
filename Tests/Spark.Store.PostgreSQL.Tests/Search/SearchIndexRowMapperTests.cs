/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Hl7.Fhir.Model;
using Hl7.Fhir.Specification;
using Spark.Engine.Core;
using Spark.Engine.Model;
using Spark.Engine.Search;
using Spark.Engine.Search.Types;
using Spark.Engine.Service.FhirServiceExtensions;
using Spark.Engine.Store.Interfaces;
using Spark.Store.PostgreSQL.Search;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Spark.Store.PostgreSQL.Tests.Search;

/// <summary>
/// Runs resources through the real <see cref="IndexService"/>, so that the mapper is tested against the
/// index values the element indexer actually produces.
/// </summary>
public class SearchIndexRowMapperTests
{
    private readonly IFhirModel _fhirModel = new FhirModel();
    private readonly IndexService _indexService;
    private readonly SearchIndexRowMapper _mapper;

    public SearchIndexRowMapperTests()
    {
        _indexService = new IndexService(
            _fhirModel,
            new DiscardingIndexStore(),
            new ElementIndexer(_fhirModel),
            new ResourceResolver(_fhirModel.SupportedResources, new PocoStructureDefinitionSummaryProvider()));
        _mapper = new SearchIndexRowMapper(_fhirModel);
    }

    [Fact]
    public async Task Map_SetsResourceTypeAndIdFromKey()
    {
        SearchIndexRows rows = await MapAsync(new Patient(), "p1");

        Assert.Equal("Patient", rows.ResourceType);
        Assert.Equal("p1", rows.ResourceId);
    }

    [Fact]
    public async Task Map_String_StoresNormalizedAndExactValue()
    {
        Patient patient = new();
        patient.Name.Add(new HumanName { Family = "Gómez", Given = ["ÅSE"] });

        SearchIndexRows rows = await MapAsync(patient);

        Assert.Contains(new StringRow("family", "gomez", "Gómez"), rows.Strings);
        Assert.Contains(new StringRow("given", "ase", "ÅSE"), rows.Strings);
        Assert.Contains(new StringRow("name", "gomez", "Gómez"), rows.Strings);
    }

    [Theory]
    [InlineData("Gómez", "gomez")]
    // Å decomposes to A and a combining ring; Æ and Ø have no decomposition and are kept.
    [InlineData("Åse Ærlig Østby", "ase ærlig østby")]
    [InlineData("Crème Brûlée", "creme brulee")]
    [InlineData("", "")]
    public void NormalizeString_LowerCasesAndRemovesDiacritics(string value, string expected)
    {
        Assert.Equal(expected, SearchIndexRowMapper.NormalizeString(value));
    }

    [Fact]
    public async Task Map_Identifier_StoresSystemAndValueAsToken()
    {
        Patient patient = new();
        patient.Identifier.Add(new Identifier("urn:oid:2.16.578.1.12.4.1.4.1", "13116900216"));

        SearchIndexRows rows = await MapAsync(patient);

        Assert.Contains(new TokenRow("identifier", "urn:oid:2.16.578.1.12.4.1.4.1", "13116900216", null), rows.Tokens);
    }

    [Fact]
    public async Task Map_CodeableConcept_StoresCodingsAndTextAsSeparateTokens()
    {
        Observation observation = new()
        {
            Code = new CodeableConcept("http://loinc.org", "8480-6", "Systolic blood pressure", "Systolic"),
        };

        SearchIndexRows rows = await MapAsync(observation);

        TokenRow[] code = rows.Tokens.Where(row => row.Param == "code").ToArray();
        Assert.Equal(2, code.Length);
        Assert.Contains(new TokenRow("code", "http://loinc.org", "8480-6", "Systolic blood pressure"), code);
        Assert.Contains(new TokenRow("code", null, null, "Systolic"), code);
    }

    [Fact]
    public async Task Map_EnumBooleanAndId_StoreCodeAsToken()
    {
        Patient patient = new() { Gender = AdministrativeGender.Female, Active = true };

        SearchIndexRows rows = await MapAsync(patient, "p1");

        Assert.Contains(new TokenRow("gender", null, "female", null), rows.Tokens);
        Assert.Contains(new TokenRow("active", null, "true", null), rows.Tokens);
        Assert.Contains(new TokenRow("_id", null, "p1", null), rows.Tokens);
    }

    [Fact]
    public async Task Map_ContactPoint_StoresCodedSystemAsString()
    {
        Patient patient = new();
        patient.Telecom.Add(new ContactPoint(ContactPoint.ContactPointSystem.Phone, ContactPoint.ContactPointUse.Mobile, "+47 99999999"));

        SearchIndexRows rows = await MapAsync(patient);

        Assert.Contains(new TokenRow("telecom", "phone", "+47 99999999", null), rows.Tokens);
    }

    [Fact]
    public async Task Map_Number_StoresDecimal()
    {
        RiskAssessment assessment = new()
        {
            Status = ObservationStatus.Final,
            Subject = new ResourceReference("Patient/p1"),
        };
        assessment.Prediction.Add(new RiskAssessment.PredictionComponent { Probability = new FhirDecimal(0.35m) });

        SearchIndexRows rows = await MapAsync(assessment);

        Assert.Contains(new NumberRow("probability", 0.35m), rows.Numbers);
    }

    [Fact]
    public async Task Map_Uri_StoresValue()
    {
        ValueSet valueSet = new() { Url = "http://example.org/fhir/ValueSet/colors" };

        SearchIndexRows rows = await MapAsync(valueSet);

        Assert.Contains(new UriRow("url", "http://example.org/fhir/ValueSet/colors"), rows.Uris);
    }

    [Fact]
    public async Task Map_SkipsInternalFields()
    {
        SearchIndexRows rows = await MapAsync(new Patient(), "p1");

        Assert.DoesNotContain(AllParams(rows), param => param.StartsWith("internal_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Map_SkipsContainedResources()
    {
        Organization organization = new() { Id = "org1", Name = "Contained Hospital" };
        Patient patient = new() { ManagingOrganization = new ResourceReference("#org1") };
        patient.Contained.Add(organization);

        SearchIndexRows rows = await MapAsync(patient);

        Assert.DoesNotContain(rows.Strings, row => row.ValueExact == "Contained Hospital");
    }

    [Fact]
    public void Map_SkipsUnknownParameters()
    {
        IndexValue root = new("root",
            new IndexValue("internal_id", new StringValue("Patient/p1")),
            new IndexValue("no-such-parameter", new StringValue("value")));

        SearchIndexRows rows = _mapper.Map(root);

        Assert.Empty(AllParams(rows));
    }

    [Fact]
    public void Map_ValueNotMatchingParameterType_IsSkipped()
    {
        IndexValue root = new("root",
            new IndexValue("internal_id", new StringValue("Patient/p1")),
            new IndexValue("family", new NumberValue(42)));

        SearchIndexRows rows = _mapper.Map(root);

        Assert.Empty(AllParams(rows));
    }

    [Fact]
    public void Map_NonRootIndexValue_Throws()
    {
        Assert.Throws<ArgumentException>(() => _mapper.Map(new IndexValue("contained")));
    }

    [Fact]
    public void Map_RootWithoutId_Throws()
    {
        Assert.Throws<ArgumentException>(() => _mapper.Map(new IndexValue("root")));
    }

    private async System.Threading.Tasks.Task<SearchIndexRows> MapAsync(Resource resource, string id = "1")
    {
        resource.Id = id;
        IndexValue root = await _indexService.IndexResourceAsync(resource, Key.Create(resource.TypeName, id, "1"));
        return _mapper.Map(root);
    }

    private static IEnumerable<string> AllParams(SearchIndexRows rows)
    {
        return rows.Strings.Select(row => row.Param)
            .Concat(rows.Tokens.Select(row => row.Param))
            .Concat(rows.Numbers.Select(row => row.Param))
            .Concat(rows.Uris.Select(row => row.Param));
    }

    private sealed class DiscardingIndexStore : IIndexStore
    {
        public Task SaveAsync(IndexValue indexValue) => Task.CompletedTask;

        public Task DeleteAsync(Entry entry) => Task.CompletedTask;

        public Task CleanAsync() => Task.CompletedTask;
    }
}
