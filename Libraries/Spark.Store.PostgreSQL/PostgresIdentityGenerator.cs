/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Hl7.Fhir.Model;
using Npgsql;
using Spark.Engine.Interfaces;
using System;
using System.Globalization;

namespace Spark.Store.PostgreSQL;

/// <summary>
/// Resource ids are UUIDv7, which sort by creation time and therefore cluster well in the
/// resources index. Version ids come from the per-resource counter in the resource_keys table,
/// which also allocates the surrogate key of the logical resource on its first version.
/// </summary>
public class PostgresIdentityGenerator : IIdentityGenerator
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresIdentityGenerator(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public string NextResourceId(Resource resource)
    {
        return Guid.CreateVersion7().ToString("D");
    }

    public string NextVersionId(string resourceIdentifier)
    {
        throw new NotSupportedException($"Use {nameof(NextVersionId)}(resourceType, resourceIdentifier) instead.");
    }

    public string NextVersionId(string resourceType, string resourceIdentifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceType);
        ArgumentException.ThrowIfNullOrEmpty(resourceIdentifier);

        // IIdentityGenerator is synchronous, so this is one of the few places the store blocks on the driver.
        using NpgsqlCommand command = _dataSource.CreateCommand(
            $"INSERT INTO {Table.ResourceKeys} (type, resource_id, last_version) VALUES (@type, @id, 1) " +
            $"ON CONFLICT (type, resource_id) DO UPDATE SET last_version = {Table.ResourceKeys}.last_version + 1 " +
            "RETURNING last_version");
        command.Parameters.AddWithValue("type", resourceType);
        command.Parameters.AddWithValue("id", resourceIdentifier);
        long next = (long)command.ExecuteScalar();
        return next.ToString(CultureInfo.InvariantCulture);
    }
}
