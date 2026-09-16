/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Hl7.Fhir.Model;
using Npgsql;
using Spark.Engine.Core;
using System;

namespace Spark.Store.PostgreSQL.Serialization;

/// <summary>
/// Reads an <see cref="Entry"/> from the resource columns shared by the resources and index_queue
/// tables. Queries must select the columns in the order of <see cref="Columns"/>.
/// </summary>
internal static class EntryRow
{
    public const string Columns = "type, resource_id, version_id, method, updated_at, body";

    private const string SubsettedTagSystem = "http://terminology.hl7.org/CodeSystem/v3-ObservationValue";

    public static Entry Read(NpgsqlDataReader reader, ResourceSerializer serializer, int offset = 0, bool subsetted = false)
    {
        string typeName = reader.GetString(offset);
        string resourceId = reader.GetString(offset + 1);
        string versionId = reader.GetString(offset + 2);
        var method = Enum.Parse<Bundle.HTTPVerb>(reader.GetString(offset + 3));
        DateTimeOffset updatedAt = reader.GetFieldValue<DateTimeOffset>(offset + 4);

        Entry entry = Entry.Create(method, Key.Create(typeName, resourceId, versionId), updatedAt);
        if (!entry.IsPresent || reader.IsDBNull(offset + 5))
            return entry;

        Resource resource = serializer.Deserialize(reader.GetString(offset + 5));
        entry.Resource = resource;

        if (subsetted)
        {
            resource.Meta ??= new Meta();
            resource.Meta.Tag.Add(new Coding
            {
                System = SubsettedTagSystem,
                Code = "SUBSETTED",
                Display = "subsetted",
            });
        }

        return entry;
    }

    public static string SerializeBody(Entry entry, ResourceSerializer serializer)
    {
        return entry.Resource is null ? null : serializer.Serialize(entry.Resource);
    }

    public static DateTime GetUpdatedAt(Entry entry)
    {
        return entry.When?.UtcDateTime ?? DateTime.UtcNow;
    }
}
