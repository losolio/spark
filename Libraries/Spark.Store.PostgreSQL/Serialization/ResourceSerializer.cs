/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Spark.Engine.Core;
using Spark.Engine.Utility;
using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Spark.Store.PostgreSQL.Serialization;

/// <summary>
/// Converts resources to and from the JSON stored in jsonb columns. Reading uses the permissive
/// ("ostrich") deserializer settings, like the MongoDB store, so stored data from older versions
/// still loads.
/// </summary>
internal sealed class ResourceSerializer
{
    private readonly BaseFhirJsonSerializer _serializer;
    private readonly BaseFhirJsonDeserializer _deserializer;

    public ResourceSerializer(IFhirModel fhirModel)
    {
        ArgumentNullException.ThrowIfNull(fhirModel);

        var inspector = fhirModel.GetModelInspector();
        _serializer = new BaseFhirJsonSerializer(inspector);
        _deserializer = new BaseFhirJsonDeserializer(inspector, DeserializerSettingsFactory.GetOstrichDeserializerSettings());
    }

    public string Serialize(Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            _serializer.Serialize(resource, writer);
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    public Resource Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        return _deserializer.Deserialize<Resource>(json);
    }
}
