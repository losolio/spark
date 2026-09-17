/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Hl7.Fhir.Model;
using Spark.Engine.Core;
using Spark.Engine.Model;
using Spark.Engine.Search.Types;
using Spark.Store.PostgreSQL.Search;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Spark.Store.PostgreSQL.Tests.Search;

public class SearchIndexRowMapperTests
{
    private readonly ResourceIndexer _indexer;
    private readonly SearchIndexRowMapper _mapper;

    public SearchIndexRowMapperTests()
    {
        IFhirModel fhirModel = new FhirModel();
        _indexer = new ResourceIndexer(fhirModel);
        _mapper = new SearchIndexRowMapper(fhirModel);
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
    public async Task Map_Date_StoresRangeCoveredByPrecision()
    {
        Patient patient = new() { BirthDate = "1980-05" };

        SearchIndexRows rows = await MapAsync(patient);

        Assert.Contains(new DateRow("birthdate",
            new DateTimeOffset(1980, 5, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(1980, 6, 1, 0, 0, 0, TimeSpan.Zero)), rows.Dates);
    }

    [Fact]
    public async Task Map_PeriodWithoutEnd_StoresUnboundedEnd()
    {
        Encounter encounter = new()
        {
            Status = Encounter.EncounterStatus.InProgress,
            Class = new Coding("http://terminology.hl7.org/CodeSystem/v3-ActCode", "AMB"),
            Period = new Period { Start = "2026-09-17T08:00:00Z" },
        };

        SearchIndexRows rows = await MapAsync(encounter);

        DateRow date = Assert.Single(rows.Dates, row => row.Param == "date");
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 8, 0, 0, TimeSpan.Zero), date.Start);
        Assert.Null(date.End);
    }

    [Fact]
    public void Map_InvertedPeriod_IsSkipped()
    {
        IndexValue root = new("root",
            new IndexValue("internal_id", new StringValue("Encounter/e1")),
            new IndexValue("date", new CompositeValue([
                new IndexValue("start", new DateTimeValue(new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero))),
                new IndexValue("end", new DateTimeValue(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero))),
            ])));

        Assert.Empty(_mapper.Map(root).Dates);
    }

    [Fact]
    public async Task Map_UcumQuantity_StoresCanonicalValue()
    {
        Observation observation = new()
        {
            Status = ObservationStatus.Final,
            Code = new CodeableConcept("http://loinc.org", "2339-0"),
            Value = new Quantity(5m, "mg", "http://unitsofmeasure.org"),
        };

        SearchIndexRows rows = await MapAsync(observation);

        QuantityRow quantity = Assert.Single(rows.Quantities, row => row.Param == "value-quantity");
        Assert.Equal("http://unitsofmeasure.org", quantity.System);
        Assert.Equal("g", quantity.Code);
        Assert.Equal(0.005m, quantity.Value);
    }

    [Fact]
    public async Task Map_NonUcumQuantity_StoresValueAndUnitAsGiven()
    {
        Observation observation = new()
        {
            Status = ObservationStatus.Final,
            Code = new CodeableConcept("http://loinc.org", "2339-0"),
            Value = new Quantity { Value = 2m, Unit = "tablets", System = "http://example.org/units" },
        };

        SearchIndexRows rows = await MapAsync(observation);

        Assert.Contains(new QuantityRow("value-quantity", "http://example.org/units", "tablets", 2m), rows.Quantities);
    }

    [Theory]
    [InlineData("Patient/p1", "Patient", "p1", null)]
    [InlineData("Patient/p1/_history/2", "Patient", "p1", null)]
    [InlineData("http://other.example.org/fhir/Patient/9", null, null, "http://other.example.org/fhir/Patient/9")]
    [InlineData("urn:uuid:5c8b6e2a-7a5d-4c4b-9d2f-1d8f0b3c4e5a", null, null, "urn:uuid:5c8b6e2a-7a5d-4c4b-9d2f-1d8f0b3c4e5a")]
    public async Task Map_Reference_StoresTargetResourceOrUrl(string reference, string type, string id, string url)
    {
        Observation observation = new()
        {
            Status = ObservationStatus.Final,
            Code = new CodeableConcept("http://loinc.org", "2339-0"),
            Subject = new ResourceReference(reference),
        };

        SearchIndexRows rows = await MapAsync(observation);

        Assert.Contains(new ReferenceRow("subject", type, id, url, null, null), rows.References);
    }

    [Theory]
    [InlineData("Unknown/p1")]
    [InlineData("p1")]
    public async Task Map_ReferenceToUnknownTypeOrWithoutType_IsSkipped(string reference)
    {
        Observation observation = new()
        {
            Status = ObservationStatus.Final,
            Code = new CodeableConcept("http://loinc.org", "2339-0"),
            Subject = new ResourceReference(reference),
        };

        SearchIndexRows rows = await MapAsync(observation);

        Assert.DoesNotContain(rows.References, row => row.Param == "subject");
    }

    [Fact]
    public async Task Map_ReferenceByIdentifier_StoresIdentifier()
    {
        Observation observation = new()
        {
            Status = ObservationStatus.Final,
            Code = new CodeableConcept("http://loinc.org", "2339-0"),
            Subject = new ResourceReference { Identifier = new Identifier("urn:oid:2.16.578.1.12.4.1.4.1", "13116900216") },
        };

        SearchIndexRows rows = await MapAsync(observation);

        Assert.Contains(
            ReferenceRow.ToIdentifier("subject", "urn:oid:2.16.578.1.12.4.1.4.1", "13116900216"),
            rows.References);
    }

    [Fact]
    public async Task Map_ReferenceToContainedResource_IsSkipped()
    {
        Organization organization = new() { Id = "org1", Name = "Contained Hospital" };
        Patient patient = new() { ManagingOrganization = new ResourceReference("#org1") };
        patient.Contained.Add(organization);

        SearchIndexRows rows = await MapAsync(patient);

        Assert.DoesNotContain(rows.References, row => row.Param == "organization");
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
        return _mapper.Map(await _indexer.IndexAsync(resource, id));
    }

    private static IEnumerable<string> AllParams(SearchIndexRows rows)
    {
        return rows.Strings.Select(row => row.Param)
            .Concat(rows.Tokens.Select(row => row.Param))
            .Concat(rows.Numbers.Select(row => row.Param))
            .Concat(rows.Uris.Select(row => row.Param))
            .Concat(rows.Dates.Select(row => row.Param))
            .Concat(rows.Quantities.Select(row => row.Param))
            .Concat(rows.References.Select(row => row.Param));
    }
}
