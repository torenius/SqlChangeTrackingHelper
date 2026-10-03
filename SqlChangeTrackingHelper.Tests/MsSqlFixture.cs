using Dapper;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace SqlChangeTracking.Tests;

/// <summary>
/// SQL Server with a database that has Change Tracking and snapshot isolation enabled,
/// since neither can be enabled on master.
/// </summary>
public sealed class MsSqlFixture : IAsyncLifetime
{
    private const string DatabaseName = "ChangeTrackingTests";

    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public string ConnectionString { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using (var connection = new SqlConnection(_container.GetConnectionString()))
        {
            await connection.ExecuteAsync($"CREATE DATABASE {DatabaseName};");
            await connection.ExecuteAsync($"ALTER DATABASE {DatabaseName} SET ALLOW_SNAPSHOT_ISOLATION ON;");
            await connection.ExecuteAsync($"ALTER DATABASE {DatabaseName} SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON);");
        }

        ConnectionString = new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = DatabaseName }.ConnectionString;
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
