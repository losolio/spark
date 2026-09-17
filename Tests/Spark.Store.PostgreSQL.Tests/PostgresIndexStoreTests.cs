/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Hl7.Fhir.Model;
using Npgsql;
using Spark.Engine.Core;
using Spark.Store.PostgreSQL.Tests.Search;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Spark.Store.PostgreSQL.Tests;

[Collection("PostgreSQL integration")]
[Trait("Category", "Integration")]
public class PostgresIndexStoreTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private ResourceIndexer _indexer;
    private PostgresIndexStore _store;

    public PostgresIndexStoreTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetAsync();
        _indexer = new ResourceIndexer(_fixture.FhirModel);
        _store = new PostgresIndexStore(_fixture.DataSource, _fixture.FhirModel);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task SaveAsync_WritesRowsToEachTable()
    {
        await SaveAsync(CreatePatient("p1", "Gómez"));
        RiskAssessment assessment = new() { Status = ObservationStatus.Final, Subject = new ResourceReference("Patient/p1") };
        assessment.Prediction.Add(new RiskAssessment.PredictionComponent { Probability = new FhirDecimal(0.35m) });
        await SaveAsync(assessment, "r1");
        await SaveAsync(new ValueSet { Url = "http://example.org/fhir/ValueSet/colors" }, "v1");

        Assert.Equal(["gomez|Gómez"], await ReadStringsAsync(
            "SELECT s.value_normalized || '|' || s.value_exact FROM search_string s " +
            "JOIN search_params p ON p.id = s.param_id WHERE p.resource_type = 'Patient' AND p.code = 'family'"));
        Assert.Equal(["urn:oid:1|12345"], await ReadStringsAsync(
            "SELECT t.system || '|' || t.code FROM search_token t " +
            "JOIN search_params p ON p.id = t.param_id WHERE p.resource_type = 'Patient' AND p.code = 'identifier'"));
        Assert.Equal(["0.35"], await ReadStringsAsync(
            "SELECT n.value::text FROM search_number n " +
            "JOIN search_params p ON p.id = n.param_id WHERE p.resource_type = 'RiskAssessment' AND p.code = 'probability'"));
        Assert.Equal(["http://example.org/fhir/ValueSet/colors"], await ReadStringsAsync(
            "SELECT u.value FROM search_uri u " +
            "JOIN search_params p ON p.id = u.param_id WHERE p.resource_type = 'ValueSet' AND p.code = 'url'"));
    }

    [Fact]
    public async Task SaveAsync_WritesDateQuantityAndReferenceRows()
    {
        Encounter encounter = new()
        {
            Status = Encounter.EncounterStatus.InProgress,
            Class = new Coding("http://terminology.hl7.org/CodeSystem/v3-ActCode", "AMB"),
            Period = new Period { Start = "2026-09-17" },
        };
        await SaveAsync(encounter, "e1");
        Observation observation = new()
        {
            Status = ObservationStatus.Final,
            Code = new CodeableConcept("http://loinc.org", "2339-0"),
            Value = new Quantity { Value = 2m, Unit = "tablets", System = "http://example.org/units" },
            Subject = new ResourceReference { Identifier = new Identifier("urn:oid:1", "12345") },
            Performer = [new ResourceReference("http://other.example.org/fhir/Practitioner/9")],
        };
        await SaveAsync(observation, "o1");

        Assert.Equal(["[\"2026-09-17 00:00:00+00\",)"], await ReadStringsAsync(
            "SELECT d.period::text FROM search_date d " +
            "JOIN search_params p ON p.id = d.param_id WHERE p.resource_type = 'Encounter' AND p.code = 'date'"));
        Assert.Equal(["http://example.org/units|tablets|2"], await ReadStringsAsync(
            "SELECT q.system || '|' || q.code || '|' || q.value::text FROM search_quantity q " +
            "JOIN search_params p ON p.id = q.param_id WHERE p.code = 'value-quantity'"));
        Assert.Equal(["urn:oid:1|12345"], await ReadStringsAsync(
            "SELECT r.identifier_system || '|' || r.identifier_value FROM search_reference r " +
            "JOIN search_params p ON p.id = r.param_id WHERE p.resource_type = 'Observation' AND p.code = 'subject'"));
        Assert.Equal(["http://other.example.org/fhir/Practitioner/9"], await ReadStringsAsync(
            "SELECT r.target_url FROM search_reference r " +
            "JOIN search_params p ON p.id = r.param_id WHERE p.resource_type = 'Observation' AND p.code = 'performer'"));
    }

    [Fact]
    public async Task SaveAsync_ReferenceToMissingResource_CreatesPlaceholderKeyThatResourceTakesOver()
    {
        await SaveAsync(CreateObservation("Patient/p1"), "o1");

        Assert.Equal(1, await CountAsync(
            "SELECT count(*) FROM resource_keys WHERE type = 'Patient' AND resource_id = 'p1' AND last_version = 0"));

        PostgresFhirStore fhirStore = new(_fixture.DataSource, _fixture.FhirModel);
        await fhirStore.AddAsync(Entry.PUT(Key.Create("Patient", "p1", "1"), new Patient { Id = "p1" }));

        Assert.Equal(1, await CountAsync("SELECT count(*) FROM resource_keys WHERE type = 'Patient'"));
        Assert.Equal(1, await CountAsync(
            "SELECT count(DISTINCT ref.target_key) FROM search_reference ref JOIN resources r ON r.resource_key = ref.target_key " +
            "WHERE r.type = 'Patient' AND r.resource_id = 'p1'"));
    }

    [Fact]
    public async Task SaveAsync_ReferenceToExistingResource_UsesItsKey()
    {
        PostgresFhirStore fhirStore = new(_fixture.DataSource, _fixture.FhirModel);
        await fhirStore.AddAsync(Entry.PUT(Key.Create("Patient", "p1", "1"), new Patient { Id = "p1" }));

        await SaveAsync(CreateObservation("Patient/p1/_history/1"), "o1");

        Assert.Equal(2, await CountAsync("SELECT count(*) FROM resource_keys"));
        Assert.Equal(1, await CountAsync(
            "SELECT count(DISTINCT ref.target_key) FROM search_reference ref JOIN resources r ON r.resource_key = ref.target_key"));
    }

    [Fact]
    public async Task SaveAsync_RepeatedReferences_ReuseTargetKey()
    {
        await SaveAsync(CreateObservation("Patient/p1"), "o1");
        await SaveAsync(CreateObservation("Patient/p1"), "o1");
        await SaveAsync(CreateObservation("Patient/p1"), "o2");

        Assert.Equal(1, await CountAsync("SELECT count(*) FROM resource_keys WHERE type = 'Patient'"));
        Assert.Equal(1, await CountAsync(
            "SELECT count(DISTINCT target_key) FROM search_reference WHERE target_key IS NOT NULL"));
    }

    [Fact]
    public async Task SaveAsync_ConcurrentlyReferencingSameMissingResource_CreatesOnePlaceholder()
    {
        IEnumerable<Task> saves = Enumerable.Range(1, 20)
            .Select(i => Task.Run(async () =>
                await _store.SaveAsync(await _indexer.IndexAsync(CreateObservation("Patient/p1"), $"o{i}"))));

        await Task.WhenAll(saves);

        Assert.Equal(1, await CountAsync("SELECT count(*) FROM resource_keys WHERE type = 'Patient'"));
        Assert.Equal(1, await CountAsync(
            "SELECT count(DISTINCT target_key) FROM search_reference WHERE target_key IS NOT NULL"));
    }

    [Fact]
    public async Task SaveAsync_SameResourceTwice_ReplacesRows()
    {
        await SaveAsync(CreatePatient("p1", "First"));
        await SaveAsync(CreatePatient("p1", "Second"));

        Assert.Equal(["Second"], await ReadFamiliesAsync());
    }

    [Fact]
    public async Task SaveAsync_UsesResourceKeyOfStoredResource()
    {
        PostgresFhirStore fhirStore = new(_fixture.DataSource, _fixture.FhirModel);
        Patient patient = CreatePatient("p1", "Losvik");
        await fhirStore.AddAsync(Entry.PUT(Key.Create("Patient", "p1", "1"), patient));

        await SaveAsync(patient);

        Assert.Equal(1, await CountAsync("SELECT count(*) FROM resource_keys"));
        Assert.Equal(1, await CountAsync(
            "SELECT count(DISTINCT s.resource_key) FROM search_string s JOIN resources r ON r.resource_key = s.resource_key"));
        Assert.Equal(0, await CountAsync(
            "SELECT count(*) FROM search_string s WHERE NOT EXISTS (SELECT 1 FROM resources r WHERE r.resource_key = s.resource_key)"));
    }

    [Fact]
    public async Task SaveAsync_ReusesSearchParamIdsAcrossResourcesAndInstances()
    {
        await SaveAsync(CreatePatient("p1", "One"));
        await new PostgresIndexStore(_fixture.DataSource, _fixture.FhirModel)
            .SaveAsync(await _indexer.IndexAsync(CreatePatient("p2", "Two"), "p2"));

        Assert.Equal(1, await CountAsync("SELECT count(*) FROM search_params WHERE resource_type = 'Patient' AND code = 'family'"));
        Assert.Equal(2, await CountAsync("SELECT count(DISTINCT resource_key) FROM search_string"));
    }

    [Fact]
    public async Task SaveAsync_KeepsSearchParamIdsContiguous()
    {
        await SaveAsync(CreatePatient("p1", "One"));
        await new PostgresIndexStore(_fixture.DataSource, _fixture.FhirModel)
            .SaveAsync(await _indexer.IndexAsync(CreatePatient("p2", "Two"), "p2"));

        // A second instance has an empty cache; looking up existing codes must not draw ids from the sequence.
        Assert.Equal(
            await CountAsync("SELECT count(*) FROM search_params"),
            await CountAsync("SELECT max(id) FROM search_params"));
    }

    [Fact]
    public async Task SaveAsync_ConcurrentlyForDifferentResources_CreatesEachSearchParamOnce()
    {
        IEnumerable<Task> saves = Enumerable.Range(1, 20)
            .Select(i => Task.Run(async () =>
            {
                // A store per caller, so every caller has to resolve the search parameter ids itself.
                PostgresIndexStore store = new(_fixture.DataSource, _fixture.FhirModel);
                await store.SaveAsync(await _indexer.IndexAsync(CreatePatient($"p{i}", $"Family{i}"), $"p{i}"));
            }));

        await Task.WhenAll(saves);

        Assert.Equal(0, await CountAsync(
            "SELECT count(*) FROM (SELECT resource_type, code FROM search_params GROUP BY 1, 2 HAVING count(*) > 1) duplicates"));
        Assert.Equal(20, await CountAsync("SELECT count(DISTINCT resource_key) FROM search_string"));
    }

    [Fact]
    public async Task SaveAsync_ConcurrentlyForSameResource_LeavesRowsOfOneSave()
    {
        IEnumerable<Task> saves = Enumerable.Range(1, 10)
            .Select(i => Task.Run(async () => await SaveAsync(CreatePatient("p1", $"Family{i}"))));

        await Task.WhenAll(saves);

        Assert.Single(await ReadFamiliesAsync());
    }

    [Fact]
    public async Task DeleteAsync_RemovesRowsOfResourceOnly()
    {
        await SaveAsync(CreatePatient("p1", "Deleted"));
        await SaveAsync(CreatePatient("p2", "Kept"), "p2");

        await _store.DeleteAsync(Entry.DELETE(Key.Create("Patient", "p1", "2"), DateTimeOffset.UtcNow));

        Assert.Equal(["Kept"], await ReadFamiliesAsync());
        Assert.Equal(0, await CountAsync(
            "SELECT count(*) FROM search_token t JOIN resource_keys k ON k.id = t.resource_key WHERE k.resource_id = 'p1'"));
    }

    [Fact]
    public async Task DeleteAsync_UnknownResource_DoesNothing()
    {
        await _store.DeleteAsync(Entry.DELETE(Key.Create("Patient", "missing", "1"), DateTimeOffset.UtcNow));

        Assert.Equal(0, await CountAsync("SELECT count(*) FROM resource_keys"));
    }

    [Fact]
    public async Task CleanAsync_RemovesAllRowsAndKeepsSearchParams()
    {
        await SaveAsync(CreatePatient("p1", "Cleaned"));
        long searchParams = await CountAsync("SELECT count(*) FROM search_params");

        await _store.CleanAsync();
        await SaveAsync(CreatePatient("p2", "After"), "p2");

        Assert.Equal(["After"], await ReadFamiliesAsync());
        Assert.Equal(searchParams, await CountAsync("SELECT count(*) FROM search_params"));
    }

    private async Task SaveAsync(Resource resource, string id = null)
    {
        await _store.SaveAsync(await _indexer.IndexAsync(resource, id ?? resource.Id ?? "1"));
    }

    private static Observation CreateObservation(string subject)
    {
        return new Observation
        {
            Status = ObservationStatus.Final,
            Code = new CodeableConcept("http://loinc.org", "2339-0"),
            Subject = new ResourceReference(subject),
        };
    }

    private static Patient CreatePatient(string id, string family)
    {
        Patient patient = new() { Id = id };
        patient.Name.Add(new HumanName { Family = family });
        patient.Identifier.Add(new Identifier("urn:oid:1", "12345"));
        return patient;
    }

    private Task<List<string>> ReadFamiliesAsync()
    {
        return ReadStringsAsync(
            "SELECT s.value_exact FROM search_string s JOIN search_params p ON p.id = s.param_id " +
            "WHERE p.resource_type = 'Patient' AND p.code = 'family' ORDER BY s.value_exact");
    }

    private async Task<List<string>> ReadStringsAsync(string sql)
    {
        List<string> values = [];
        await using NpgsqlCommand command = _fixture.DataSource.CreateCommand(sql);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private async Task<long> CountAsync(string sql)
    {
        await using NpgsqlCommand command = _fixture.DataSource.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
