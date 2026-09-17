/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Spark.Engine;
using Spark.Engine.Core;
using Spark.Engine.Interfaces;
using Spark.Engine.Store.Interfaces;
using Spark.Store.PostgreSQL.Extensions;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Spark.Store.PostgreSQL.Tests.Extensions;

[Collection("PostgreSQL integration")]
[Trait("Category", "Integration")]
public class IServiceCollectionExtensionsTests
{
    private readonly PostgresFixture _fixture;

    public IServiceCollectionExtensionsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AddPostgresFhirStore_RegistersStoreImplementations()
    {
        string connectionString = await _fixture.CreateDatabaseConnectionStringAsync("di");
        await using ServiceProvider provider = CreateServices(connectionString).BuildServiceProvider();

        Assert.IsType<PostgresIdentityGenerator>(provider.GetRequiredService<IIdentityGenerator>());
        Assert.IsType<PostgresFhirStore>(provider.GetRequiredService<IFhirStore>());
        Assert.IsType<PostgresFhirStorePagedReader>(provider.GetRequiredService<IFhirStorePagedReader>());
        Assert.IsType<PostgresHistoryStore>(provider.GetRequiredService<IHistoryStore>());
        Assert.IsType<PostgresSnapshotStore>(provider.GetRequiredService<ISnapshotStore>());
        Assert.IsType<PostgresSnapshotStore>(provider.GetRequiredService<ISnapshotStore2>());
        Assert.IsType<PostgresStoreAdministration>(provider.GetRequiredService<IFhirStoreAdministration>());
        Assert.IsType<PostgresFhirIndex>(provider.GetRequiredService<IFhirIndex>());
        Assert.IsType<PostgresIndexStore>(provider.GetRequiredService<IIndexStore>());
        Assert.Same(provider.GetRequiredService<IIndexStore>(), provider.GetRequiredService<IIndexStore>());
    }

    [Fact]
    public async Task AddPostgresFhirStore_CreatesSchemaBeforeHostedServicesStart()
    {
        string connectionString = await _fixture.CreateDatabaseConnectionStringAsync("di");
        await using ServiceProvider provider = CreateServices(connectionString).BuildServiceProvider();

        IHostedLifecycleService schemaService = Assert.Single(provider.GetServices<IHostedService>().OfType<IHostedLifecycleService>());
        await schemaService.StartingAsync(TestContext.Current.CancellationToken);

        await using NpgsqlCommand command = provider.GetRequiredService<NpgsqlDataSource>().CreateCommand(
            "SELECT count(*) FROM information_schema.tables WHERE table_name = 'resources'");
        Assert.Equal(1L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static ServiceCollection CreateServices(string connectionString)
    {
        ServiceCollection services = new();
        services.AddSingleton<IFhirModel>(new FhirModel());
        services.AddPostgresFhirStore(new StoreSettings { ConnectionString = connectionString });
        return services;
    }
}
