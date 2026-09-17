/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Spark.Engine;
using Spark.Engine.Core;
using Spark.Engine.Interfaces;
using Spark.Engine.Search;
using Spark.Engine.Store.Interfaces;

namespace Spark.Store.PostgreSQL.Extensions;

public static class IServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PostgreSQL store. <see cref="StoreSettings.ConnectionString"/> is an Npgsql connection
    /// string, and the schema is created or upgraded when the host starts.
    /// </summary>
    /// <remarks>
    /// Background indexing (<c>IndexingMode.Background</c>) and data migrations
    /// (<see cref="IDatabaseMigrationService"/>) are not supported by the PostgreSQL store yet.
    /// </remarks>
    public static void AddPostgresFhirStore(this IServiceCollection services, StoreSettings settings)
    {
        services.TryAddSingleton(settings);
        services.TryAddSingleton(_ => NpgsqlDataSource.Create(settings.ConnectionString));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PostgresSchemaService>());

        services.TryAddTransient<IIdentityGenerator>(provider => new PostgresIdentityGenerator(
            provider.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddTransient<IFhirStore>(provider => new PostgresFhirStore(
            provider.GetRequiredService<NpgsqlDataSource>(), provider.GetRequiredService<IFhirModel>()));
        services.TryAddTransient<IFhirStorePagedReader>(provider => new PostgresFhirStorePagedReader(
            provider.GetRequiredService<NpgsqlDataSource>(), provider.GetRequiredService<IFhirModel>()));
        services.TryAddTransient<IHistoryStore>(provider => new PostgresHistoryStore(
            provider.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddTransient<ISnapshotStore>(provider => new PostgresSnapshotStore(
            provider.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddTransient<ISnapshotStore2>(provider => new PostgresSnapshotStore(
            provider.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddTransient<IFhirStoreAdministration>(provider => new PostgresStoreAdministration(
            provider.GetRequiredService<NpgsqlDataSource>()));

        // A singleton, so that its cache of search parameter ids is shared by all requests.
        services.TryAddSingleton<IIndexStore>(provider => new PostgresIndexStore(
            provider.GetRequiredService<NpgsqlDataSource>(), provider.GetRequiredService<IFhirModel>()));
        services.TryAddTransient<IFhirIndex>(provider => new PostgresFhirIndex(
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<IFhirModel>(),
            provider.GetService<IReferenceNormalizationService>()));
    }
}
