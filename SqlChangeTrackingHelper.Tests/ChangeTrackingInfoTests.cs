using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Shouldly;

namespace SqlChangeTracking.Tests;

public class ChangeTrackingInfoTests(MsSqlFixture fixture) : IClassFixture<MsSqlFixture>
{
    private readonly string _connectionString = fixture.ConnectionString;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string NewTableName() => $"Test_{Guid.NewGuid():N}";

    [Fact]
    public async Task DatabaseSettings()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(Token);

        var info = await SqlChangeTrackingHelper.GetChangeTrackingInfoAsync(connection, cancellationToken: Token);

        // As set up in MsSqlFixture
        info.DatabaseName.ShouldBe("ChangeTrackingTests");
        info.Retention.ShouldBe(TimeSpan.FromDays(2));
        info.AutoCleanup.ShouldBeTrue();
        info.SnapshotIsolationAllowed.ShouldBeTrue();
        info.CurrentVersion.ShouldBe(await connection.ExecuteScalarAsync<long>("SELECT CHANGE_TRACKING_CURRENT_VERSION();"));
    }

    [Fact]
    public async Task Tables_OnlyTrackedWithPrimaryKeyAndSettings()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        var tracked = NewTableName();
        var trackedColumns = NewTableName();
        var notTracked = NewTableName();

        await connection.ExecuteAsync($"""
            CREATE TABLE dbo.{tracked} (TenantId int NOT NULL, Sku varchar(10) NOT NULL, Name nvarchar(20) NULL, PRIMARY KEY (TenantId, Sku));
            ALTER TABLE dbo.{tracked} ENABLE CHANGE_TRACKING;
            CREATE TABLE dbo.{trackedColumns} (Id int PRIMARY KEY);
            ALTER TABLE dbo.{trackedColumns} ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = ON);
            CREATE TABLE dbo.{notTracked} (Id int PRIMARY KEY);
            """);

        var info = await SqlChangeTrackingHelper.GetChangeTrackingInfoAsync(connection, cancellationToken: Token);
        var tables = info.Tables.ToDictionary(x => x.TableName);

        tables.ShouldNotContainKey(notTracked);

        var table = tables[tracked];
        table.SchemaName.ShouldBe("dbo");
        table.QuotedName.ShouldBe($"[dbo].[{tracked}]");
        table.PrimaryKey.ShouldBe(["TenantId", "Sku"]);
        table.TrackColumnsUpdated.ShouldBeFalse();
        table.BeginVersion.ShouldBeLessThanOrEqualTo(info.CurrentVersion);
        table.Rows.ShouldBeNull();
        table.DataSizeMb.ShouldBeNull();
        table.ChangeTrackingRows.ShouldBeNull();
        table.ChangeTrackingSizeMb.ShouldBeNull();

        tables[trackedColumns].TrackColumnsUpdated.ShouldBeTrue();
        tables[trackedColumns].PrimaryKey.ShouldBe(["Id"]);
    }

    [Fact]
    public async Task IncludeSizes_PartitionedTable_RowsAreNotMultiplied()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        var tableName = NewTableName();

        await connection.ExecuteAsync($"""
            CREATE PARTITION FUNCTION PF_{tableName} (int) AS RANGE LEFT FOR VALUES (100, 200);
            CREATE PARTITION SCHEME PS_{tableName} AS PARTITION PF_{tableName} ALL TO ([PRIMARY]);
            CREATE TABLE dbo.{tableName} (Id int PRIMARY KEY, Name nvarchar(20) NOT NULL) ON PS_{tableName} (Id);
            ALTER TABLE dbo.{tableName} ENABLE CHANGE_TRACKING;
            """);

        // 300 rows over the 3 partitions, and then 50 of them changed
        await connection.ExecuteAsync($"INSERT INTO dbo.{tableName} (Id, Name) VALUES (@Id, 'Name')", Enumerable.Range(1, 300).Select(x => new { Id = x }));
        await connection.ExecuteAsync($"UPDATE dbo.{tableName} SET Name = 'Changed' WHERE Id <= 50;");

        var info = await SqlChangeTrackingHelper.GetChangeTrackingInfoAsync(connection, includeSizes: true, cancellationToken: Token);
        var table = info.Tables.Single(x => x.TableName == tableName);

        table.Rows.ShouldBe(300);
        table.DataSizeMb.ShouldNotBeNull();
        table.DataSizeMb.Value.ShouldBeGreaterThan(0);
        table.ChangeTrackingRows.ShouldNotBeNull();
        table.ChangeTrackingRows.Value.ShouldBeGreaterThan(0);
        table.ChangeTrackingSizeMb.ShouldNotBeNull();
    }

    [Fact]
    public async Task CanReadChangesSince_SameRuleAsReadChangesAsync()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        var tableName = NewTableName();
        await connection.ExecuteAsync($"""
            CREATE TABLE dbo.{tableName} (Id int PRIMARY KEY, Name nvarchar(20) NULL);
            ALTER TABLE dbo.{tableName} ENABLE CHANGE_TRACKING;
            INSERT INTO dbo.{tableName} VALUES (1, 'A');
            """);

        var oldVersion = await connection.ExecuteScalarAsync<long>("SELECT CHANGE_TRACKING_CURRENT_VERSION();");
        await connection.ExecuteAsync($"UPDATE dbo.{tableName} SET Name = 'B'; ALTER TABLE dbo.{tableName} DISABLE CHANGE_TRACKING; ALTER TABLE dbo.{tableName} ENABLE CHANGE_TRACKING;");

        var info = await SqlChangeTrackingHelper.GetChangeTrackingInfoAsync(connection, cancellationToken: Token);
        var table = info.Tables.Single(x => x.TableName == tableName);

        table.CanReadChangesSince(oldVersion).ShouldBeFalse();
        table.CanReadChangesSince(table.MinValidVersion).ShouldBeTrue();
        table.CanReadChangesSince(info.CurrentVersion).ShouldBeTrue();
        table.CanReadChangesSince(info.CurrentVersion + 1).ShouldBeFalse();

        // ReadChangesAsync agrees
        await Should.ThrowAsync<ChangeTrackingVersionTooOldException>(async () =>
            await new SqlChangeTrackingHelper(table.QuotedName).ReadChangesAsync(connection, oldVersion, cancellationToken: Token));

        await using var changes = await new SqlChangeTrackingHelper(table.QuotedName).ReadChangesAsync(connection, table.MinValidVersion, cancellationToken: Token);
        changes.IsFullLoad.ShouldBeFalse();
    }

    [Fact]
    public async Task ClosedConnection_IsClosedAgain()
    {
        await using var connection = new SqlConnection(_connectionString);

        await SqlChangeTrackingHelper.GetChangeTrackingInfoAsync(connection, includeSizes: true, cancellationToken: Token);

        connection.State.ShouldBe(ConnectionState.Closed);
    }

    [Fact]
    public async Task ChangeTrackingNotEnabledForDatabase_Throws()
    {
        var master = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" }.ConnectionString;
        await using var connection = new SqlConnection(master);

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await SqlChangeTrackingHelper.GetChangeTrackingInfoAsync(connection, cancellationToken: Token));

        exception.Message.ShouldContain("ALTER DATABASE [master] SET CHANGE_TRACKING = ON");
        connection.State.ShouldBe(ConnectionState.Closed);
    }
}
