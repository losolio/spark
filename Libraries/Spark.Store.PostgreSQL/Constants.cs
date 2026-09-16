/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

namespace Spark.Store.PostgreSQL;

internal static class Table
{
    public const string ResourceKeys = "resource_keys";
    public const string Resources = "resources";
    public const string Snapshots = "snapshots";
    public const string IndexQueue = "index_queue";
    public const string DatabaseMigrations = "database_migrations";
    public const string SchemaHistory = "schema_history";
}

internal static class ResourceState
{
    public const string Current = "current";
    public const string Superseded = "superseded";
}

internal static class IndexQueueStatus
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Failed = "failed";
}
