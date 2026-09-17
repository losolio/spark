/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Npgsql;
using Spark.Engine.Core;
using Spark.Engine.Search;
using Spark.Store.PostgreSQL.Tests.Search;
using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Spark.Store.PostgreSQL.Tests;

[Collection("PostgreSQL integration")]
[Trait("Category", "Integration")]
public class PostgresFhirIndexTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;
    private PostgresFhirStore _store;
    private PostgresIndexStore _indexStore;
    private ResourceIndexer _indexer;
    private PostgresFhirIndex _index;

    public PostgresFhirIndexTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetAsync();
        _store = new PostgresFhirStore(_fixture.DataSource, _fixture.FhirModel);
        _indexStore = new PostgresIndexStore(_fixture.DataSource, _fixture.FhirModel);
        _indexer = new ResourceIndexer(_fixture.FhirModel);
        _index = new PostgresFhirIndex(_fixture.DataSource, _fixture.FhirModel);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task SearchAsync_WithoutCriteria_ReturnsCurrentVersionsOfTypeNewestFirst()
    {
        await AddAsync("Patient", "p1", "1", Now.AddMinutes(1));
        await AddAsync("Patient", "p2", "1", Now.AddMinutes(2));
        await AddAsync("Patient", "p1", "2", Now.AddMinutes(3));
        await AddAsync("Observation", "o1", "1", Now.AddMinutes(4));

        SearchResults results = await _index.SearchAsync("Patient", new SearchParams());

        Assert.Equal(["Patient/p1/_history/2", "Patient/p2/_history/1"], results);
        Assert.Equal(2, results.MatchCount);
        Assert.False(results.HasIssues);
    }

    [Fact]
    public async Task SearchAsync_ExcludesDeletedResources()
    {
        await AddAsync("Patient", "p1", "1", Now);
        await AddAsync("Patient", "p2", "1", Now);
        await _store.AddAsync(Entry.DELETE(Key.Create("Patient", "p1", "2"), Now.AddMinutes(1)));

        SearchResults results = await _index.SearchAsync("Patient", new SearchParams());

        Assert.Equal(["Patient/p2/_history/1"], results);
    }

    [Fact]
    public async Task SearchAsync_ById_ReturnsMatchingResource()
    {
        await AddAsync("Patient", "p1", "1", Now);
        await AddAsync("Patient", "p2", "1", Now);
        await AddAsync("Observation", "p1", "1", Now);

        SearchResults results = await _index.SearchAsync("Patient", new SearchParams().Add("_id", "p1"));

        Assert.Equal(["Patient/p1/_history/1"], results);
        Assert.Equal("_id=p1", results.UsedParameters);
    }

    [Fact]
    public async Task SearchAsync_ByMultipleIds_ReturnsEachMatch()
    {
        await AddAsync("Patient", "p1", "1", Now.AddMinutes(1));
        await AddAsync("Patient", "p2", "1", Now.AddMinutes(2));
        await AddAsync("Patient", "p3", "1", Now.AddMinutes(3));

        SearchResults results = await _index.SearchAsync("Patient", new SearchParams().Add("_id", "p1,p3,missing"));

        Assert.Equal(["Patient/p3/_history/1", "Patient/p1/_history/1"], results);
    }

    [Fact]
    public async Task SearchAsync_ByUnknownId_ReturnsNothing()
    {
        await AddAsync("Patient", "p1", "1", Now);

        Assert.Empty(await _index.SearchAsync("Patient", new SearchParams().Add("_id", "missing")));
    }

    [Fact]
    public async Task SearchAsync_SortByLastUpdated_ReturnsOldestFirst()
    {
        await AddAsync("Patient", "p1", "1", Now.AddMinutes(2));
        await AddAsync("Patient", "p2", "1", Now.AddMinutes(1));

        SearchParams searchParams = new();
        searchParams.Sort.Add(("_lastUpdated", SortOrder.Ascending));
        SearchResults results = await _index.SearchAsync("Patient", searchParams);

        Assert.Equal(["Patient/p2/_history/1", "Patient/p1/_history/1"], results);
    }

    [Theory]
    [InlineData("_lastUpdated", "2026-09-17", "p2")]
    [InlineData("_lastUpdated", "eq2026-09-17", "p2")]
    [InlineData("_lastUpdated", "ap2026-09-17", "p2")]
    [InlineData("_lastUpdated", "ne2026-09-17", "p1,p3")]
    [InlineData("_lastUpdated", "gt2026-09-17", "p3")]
    [InlineData("_lastUpdated", "sa2026-09-17", "p3")]
    [InlineData("_lastUpdated", "ge2026-09-17", "p2,p3")]
    [InlineData("_lastUpdated", "lt2026-09-17", "p1")]
    [InlineData("_lastUpdated", "eb2026-09-17", "p1")]
    [InlineData("_lastUpdated", "le2026-09-17", "p1,p2")]
    [InlineData("_lastUpdated", "2026-09", "p1,p2,p3")]
    [InlineData("_lastUpdated", "gt1900-01-01", "p1,p2,p3")]
    [InlineData("_lastUpdated", "2026-09-17T12:00:00Z", "p2")]
    [InlineData("_lastUpdated", "2026-09-17T12:00:01Z", "")]
    [InlineData("_lastUpdated", "2026-09-16,2026-09-18", "p1,p3")]
    [InlineData("_lastUpdated:missing", "true", "")]
    [InlineData("_lastUpdated:missing", "false", "p1,p2,p3")]
    public async Task SearchAsync_ByLastUpdated_ComparesWithPrecisionOfValue(string name, string value, string expected)
    {
        await AddAsync("Patient", "p1", "1", Now.AddDays(-1));
        await AddAsync("Patient", "p2", "1", Now);
        await AddAsync("Patient", "p3", "1", Now.AddDays(1));

        SearchResults results = await _index.SearchAsync("Patient", new SearchParams().Add(name, value));

        Assert.Equal(
            expected.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(id => $"Patient/{id}/_history/1"),
            results.Order());
    }

    [Fact]
    public async Task SearchAsync_ByLastUpdatedAndId_CombinesCriteria()
    {
        await AddAsync("Patient", "p1", "1", Now.AddDays(-1));
        await AddAsync("Patient", "p2", "1", Now);

        SearchResults results = await _index.SearchAsync("Patient",
            new SearchParams().Add("_lastUpdated", "ge2026-09-16").Add("_id", "p2"));

        Assert.Equal(["Patient/p2/_history/1"], results);
        Assert.Equal(2, results.UsedCriteria.Count);
    }

    [Fact]
    public async Task SearchAsync_ByInvalidLastUpdated_ThrowsBadRequest()
    {
        SparkException exception = await Assert.ThrowsAsync<SparkException>(
            () => _index.SearchAsync("Patient", new SearchParams().Add("_lastUpdated", "not-a-date")));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
    }

    [Fact]
    public async Task SearchAsync_UnknownParameter_IsIgnoredWithWarning()
    {
        await AddAsync("Patient", "p1", "1", Now);

        SearchResults results = await _index.SearchAsync("Patient", new SearchParams().Add("no-such-parameter", "x"));

        Assert.Equal(["Patient/p1/_history/1"], results);
        Assert.True(results.HasIssues);
        Assert.False(results.HasErrors);
        Assert.Equal(OperationOutcome.IssueSeverity.Warning, Assert.Single(results.Outcome.Issue).Severity);
        Assert.Empty(results.UsedCriteria);
    }

    [Theory]
    [InlineData("family", "gom", "p1")]
    [InlineData("family", "GÓM", "p1")]
    [InlineData("family", "Gómez", "p1")]
    [InlineData("family", "mez", "")]
    [InlineData("family:contains", "OME", "p1")]
    [InlineData("family:exact", "Gómez", "p1")]
    [InlineData("family:exact", "gomez", "")]
    [InlineData("family", "gom,han", "p1,p2")]
    [InlineData("given", "åse", "p1")]
    [InlineData("name", "hansen", "p2")]
    [InlineData("family", "a_b", "p3")]
    [InlineData("family", "a%", "")]
    [InlineData("family", "a\\,b", "p4")]
    public async Task SearchAsync_ByString_MatchesStartCaseAndAccentInsensitively(string name, string value, string expected)
    {
        await AddIndexedAsync(CreatePatient("p1", "Gómez", given: "Åse"));
        await AddIndexedAsync(CreatePatient("p2", "Hansen"));
        await AddIndexedAsync(CreatePatient("p3", "a_b"));
        await AddIndexedAsync(CreatePatient("p4", "a,b"));
        await AddIndexedAsync(CreatePatient("p5", "axb"));

        SearchResults results = await _index.SearchAsync("Patient", new SearchParams().Add(name, value));

        AssertIds(expected, results);
    }

    [Theory]
    [InlineData("identifier", "12345", "p1,p2")]
    [InlineData("identifier", "urn:oid:1|12345", "p1")]
    [InlineData("identifier", "|12345", "p2")]
    [InlineData("identifier", "urn:oid:1|", "p1,p3")]
    [InlineData("identifier", "urn:oid:1|12345,urn:oid:1|67890", "p1,p3")]
    [InlineData("identifier", "urn:oid:2|12345", "")]
    [InlineData("gender", "female", "p1")]
    [InlineData("gender:not", "female", "p2,p3")]
    [InlineData("gender:missing", "true", "p3")]
    [InlineData("gender:missing", "false", "p1,p2")]
    [InlineData("active", "true", "p2")]
    public async Task SearchAsync_ByToken_MatchesSystemAndCode(string name, string value, string expected)
    {
        Patient p1 = CreatePatient("p1", "One");
        p1.Identifier.Add(new Identifier("urn:oid:1", "12345"));
        p1.Gender = AdministrativeGender.Female;
        Patient p2 = CreatePatient("p2", "Two");
        p2.Identifier.Add(new Identifier(null, "12345"));
        p2.Gender = AdministrativeGender.Male;
        p2.Active = true;
        Patient p3 = CreatePatient("p3", "Three");
        p3.Identifier.Add(new Identifier("urn:oid:1", "67890"));
        await AddIndexedAsync(p1);
        await AddIndexedAsync(p2);
        await AddIndexedAsync(p3);

        SearchResults results = await _index.SearchAsync("Patient", new SearchParams().Add(name, value));

        AssertIds(expected, results);
    }

    [Fact]
    public async Task SearchAsync_ByTokenWithEscapedSeparator_MatchesCodeContainingSeparator()
    {
        Patient patient = CreatePatient("p1", "One");
        patient.Identifier.Add(new Identifier("urn:oid:1", "a|b"));
        await AddIndexedAsync(patient);

        SearchResults results = await _index.SearchAsync("Patient", new SearchParams().Add("identifier", @"urn:oid:1|a\|b"));

        AssertIds("p1", results);
    }

    [Fact]
    public async Task SearchAsync_ByStringAndToken_CombinesCriteria()
    {
        Patient p1 = CreatePatient("p1", "Hansen");
        p1.Gender = AdministrativeGender.Female;
        Patient p2 = CreatePatient("p2", "Hansen");
        p2.Gender = AdministrativeGender.Male;
        await AddIndexedAsync(p1);
        await AddIndexedAsync(p2);

        SearchResults results = await _index.SearchAsync("Patient",
            new SearchParams().Add("family", "hansen").Add("gender", "male"));

        AssertIds("p2", results);
        Assert.Equal(1, await _index.CountAsync("Patient", new SearchParams().Add("family", "hansen").Add("gender", "male")));
    }

    [Fact]
    public async Task SearchAsync_ExcludesResourceWhoseIndexWasDeleted()
    {
        await AddIndexedAsync(CreatePatient("p1", "Hansen"));
        await _indexStore.DeleteAsync(Entry.DELETE(Key.Create("Patient", "p1", "2"), Now));

        Assert.Empty(await _index.SearchAsync("Patient", new SearchParams().Add("family", "hansen")));
    }

    [Theory]
    [InlineData("subject=Patient/pa", "o1")]
    [InlineData("subject=pa", "o1,o5")]
    [InlineData("subject:Patient=pa", "o1")]
    [InlineData("subject:Group=Patient/pa", "")]
    [InlineData("subject=Patient/pa/_history/3", "o1")]
    [InlineData("subject=Patient/pa,Patient/pb", "o1,o2")]
    [InlineData("subject=http://other.example.org/fhir/Patient/9", "o4")]
    [InlineData("subject=http://localhost/fhir/Patient/pa", "o1")]
    [InlineData("subject:identifier=urn:oid:1|111", "o3")]
    [InlineData("patient=Patient/pa", "o1")]
    [InlineData("performer=Practitioner/pr1", "o2")]
    [InlineData("performer:missing=true", "o1,o3,o4,o5")]
    [InlineData("subject:Patient.name=hansen", "o1")]
    [InlineData("subject.name=olsen", "o2")]
    [InlineData("subject._id=pb", "o2")]
    [InlineData("patient.identifier=urn:oid:1|111", "o1")]
    [InlineData("subject:Patient.general-practitioner.name=legesen", "o1")]
    [InlineData("subject:Patient.general-practitioner:Practitioner._id=pr1", "o1")]
    public async Task SearchAsync_ByReference_MatchesTargetUrlIdentifierOrChain(string parameter, string expected)
    {
        await AddReferenceDataAsync();
        PostgresFhirIndex index = new(_fixture.DataSource, _fixture.FhirModel,
            new ReferenceNormalizationService(new Localhost(new Uri("http://localhost/fhir"))));
        string[] nameAndValue = parameter.Split('=', 2);

        SearchResults results = await index.SearchAsync("Observation", new SearchParams().Add(nameAndValue[0], nameAndValue[1]));

        Assert.Equal(
            expected.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(id => $"Observation/{id}/_history/1"),
            results.Order());
    }

    [Fact]
    public async Task SearchAsync_ChainWithUnknownParameter_IsIgnoredWithWarning()
    {
        await AddReferenceDataAsync();

        SearchResults results = await _index.SearchAsync("Observation", new SearchParams().Add("subject.no-such-parameter", "x"));

        Assert.Equal(5, results.Count);
        Assert.Equal(OperationOutcome.IssueSeverity.Warning, Assert.Single(results.Outcome.Issue).Severity);
    }

    [Fact]
    public async Task SearchAsync_ByReferenceToResourceCreatedLater_MatchesIt()
    {
        await AddIndexedAsync(CreateObservation("o1", "Patient/later"));
        await AddIndexedAsync(CreatePatient("later", "Later"));

        SearchResults results = await _index.SearchAsync("Observation", new SearchParams().Add("subject.name", "later"));

        Assert.Equal(["Observation/o1/_history/1"], results);
    }

    [Theory]
    [InlineData("birthdate", "2000-01-01")]
    [InlineData("gender:text", "female")]
    [InlineData("family:missing-modifier", "x")]
    [InlineData("general-practitioner:unknown-modifier", "x")]
    public async Task SearchAsync_ParameterNotImplemented_ThrowsNotImplemented(string name, string value)
    {
        SparkException exception = await Assert.ThrowsAsync<SparkException>(
            () => _index.SearchAsync("Patient", new SearchParams().Add(name, value)));

        Assert.Equal(HttpStatusCode.NotImplemented, exception.StatusCode);
    }

    [Fact]
    public async Task SearchAsync_SortNotImplemented_ThrowsNotImplemented()
    {
        SearchParams searchParams = new();
        searchParams.Sort.Add(("birthdate", SortOrder.Ascending));

        SparkException exception = await Assert.ThrowsAsync<SparkException>(
            () => _index.SearchAsync("Patient", searchParams));

        Assert.Equal(HttpStatusCode.NotImplemented, exception.StatusCode);
    }

    [Fact]
    public async Task CountAsync_CountsMatches()
    {
        await AddAsync("Patient", "p1", "1", Now);
        await AddAsync("Patient", "p1", "2", Now);
        await AddAsync("Patient", "p2", "1", Now);

        Assert.Equal(2, await _index.CountAsync("Patient", new SearchParams()));
        Assert.Equal(1, await _index.CountAsync("Patient", new SearchParams().Add("_id", "p2")));
    }

    [Fact]
    public async Task FindSingleAsync_ReturnsKeyOfOnlyMatch()
    {
        await AddAsync("Patient", "p1", "1", Now);
        await AddAsync("Patient", "p2", "1", Now);

        Key key = await _index.FindSingleAsync("Patient", new SearchParams().Add("_id", "p2"));

        Assert.Equal("Patient/p2/_history/1", key.ToString());
    }

    [Fact]
    public async Task FindSingleAsync_WithoutSingleMatch_ThrowsBadRequest()
    {
        await AddAsync("Patient", "p1", "1", Now);
        await AddAsync("Patient", "p2", "1", Now);

        SparkException none = await Assert.ThrowsAsync<SparkException>(
            () => _index.FindSingleAsync("Patient", new SearchParams().Add("_id", "missing")));
        SparkException many = await Assert.ThrowsAsync<SparkException>(
            () => _index.FindSingleAsync("Patient", new SearchParams()));

        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, many.StatusCode);
    }

    [Fact]
    public async Task CleanAsync_ErasesSearchIndex()
    {
        Patient patient = new() { Id = "p1" };
        patient.Name.Add(new HumanName { Family = "Erased" });
        await new PostgresIndexStore(_fixture.DataSource, _fixture.FhirModel)
            .SaveAsync(await new ResourceIndexer(_fixture.FhirModel).IndexAsync(patient, "p1"));

        await _index.CleanAsync();

        await using NpgsqlCommand command = _fixture.DataSource.CreateCommand("SELECT count(*) FROM search_string");
        Assert.Equal(0L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private async Task AddIndexedAsync(Resource resource)
    {
        await AddAsync(resource, "1", Now);
        await _indexStore.SaveAsync(await _indexer.IndexAsync(resource, resource.Id));
    }

    /// <summary>
    /// Observations o1 to o5 with subjects Patient/pa, Patient/pb, a patient identifier, a URL of another server
    /// and Group/pa. Patient pa is named Hansen, has identifier urn:oid:1|111 and general practitioner pr1 named
    /// Legesen, who performed o2 for Patient pb named Olsen.
    /// </summary>
    private async Task AddReferenceDataAsync()
    {
        Practitioner practitioner = new() { Id = "pr1" };
        practitioner.Name.Add(new HumanName { Family = "Legesen" });
        Patient pa = CreatePatient("pa", "Hansen");
        pa.Identifier.Add(new Identifier("urn:oid:1", "111"));
        pa.GeneralPractitioner.Add(new ResourceReference("Practitioner/pr1"));
        Group group = new() { Id = "pa", Type = Group.GroupType.Person, Actual = true, Name = "Hansen family" };

        await AddIndexedAsync(practitioner);
        await AddIndexedAsync(pa);
        await AddIndexedAsync(CreatePatient("pb", "Olsen"));
        await AddIndexedAsync(group);

        Observation o2 = CreateObservation("o2", "Patient/pb");
        o2.Performer.Add(new ResourceReference("Practitioner/pr1"));
        Observation o3 = CreateObservation("o3", null);
        o3.Subject = new ResourceReference { Identifier = new Identifier("urn:oid:1", "111") };

        await AddIndexedAsync(CreateObservation("o1", "Patient/pa"));
        await AddIndexedAsync(o2);
        await AddIndexedAsync(o3);
        await AddIndexedAsync(CreateObservation("o4", "http://other.example.org/fhir/Patient/9"));
        await AddIndexedAsync(CreateObservation("o5", "Group/pa"));
    }

    private static Observation CreateObservation(string id, string subject)
    {
        return new Observation
        {
            Id = id,
            Status = ObservationStatus.Final,
            Code = new CodeableConcept("http://loinc.org", "2339-0"),
            Subject = subject == null ? null : new ResourceReference(subject),
        };
    }

    private static Patient CreatePatient(string id, string family, string given = null)
    {
        Patient patient = new() { Id = id };
        HumanName name = new() { Family = family };
        if (given != null)
            name.Given = [given];
        patient.Name.Add(name);
        return patient;
    }

    private static void AssertIds(string expected, SearchResults results)
    {
        Assert.Equal(
            expected.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(id => $"Patient/{id}/_history/1"),
            results.Order());
    }

    private Task<Entry> AddAsync(Resource resource, string version, DateTimeOffset when)
    {
        Entry entry = Entry.PUT(Key.Create(resource.TypeName, resource.Id, version), resource);
        entry.When = when;
        return _store.AddAsync(entry);
    }

    private Task<Entry> AddAsync(string type, string id, string version, DateTimeOffset when)
    {
        Resource resource = type == "Patient" ? new Patient { Id = id } : new Observation { Id = id };
        Entry entry = Entry.PUT(Key.Create(type, id, version), resource);
        entry.When = when;
        return _store.AddAsync(entry);
    }
}
