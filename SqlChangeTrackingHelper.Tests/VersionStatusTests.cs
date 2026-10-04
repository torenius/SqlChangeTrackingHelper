using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Shouldly;

namespace SqlChangeTracking.Tests;

public class VersionStatusTests(MsSqlFixture fixture) : IClassFixture<MsSqlFixture>
{
    private readonly string _connectionString = fixture.ConnectionString;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<string> CreateTableAsync(SqlConnection connection)
    {
        var tableName = $"dbo.Test_{Guid.NewGuid():N}";
        await connection.ExecuteAsync($"""
            CREATE TABLE {tableName} (Id int PRIMARY KEY, Name nvarchar(20) NULL);
            ALTER TABLE {tableName} ENABLE CHANGE_TRACKING;
            INSERT INTO {tableName} VALUES (1, 'A');
            """);
        return tableName;
    }

    private static Task<long> CurrentVersionAsync(SqlConnection connection) =>
        connection.ExecuteScalarAsync<long>("SELECT CHANGE_TRACKING_CURRENT_VERSION();");

    [Fact]
    public async Task RecentVersion()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        var tableName = await CreateTableAsync(connection);
        var version = await CurrentVersionAsync(connection);

        var status = await new SqlChangeTrackingHelper(tableName).GetVersionStatusAsync(connection, version, cancellationToken: Token);

        status.Version.ShouldBe(version);
        status.CurrentVersion.ShouldBe(version);
        status.CanReadChanges.ShouldBeTrue();
        status.Retention.ShouldBe(TimeSpan.FromDays(2));

        // The commit time of exactly that version, as UTC
        var commitTime = await connection.ExecuteScalarAsync<DateTime>("SELECT commit_time FROM sys.dm_tran_commit_table WHERE commit_ts = @version", new { version });
        status.CommittedAt.ShouldBe(DateTime.SpecifyKind(commitTime, DateTimeKind.Utc));
        status.CommittedAt!.Value.Kind.ShouldBe(DateTimeKind.Utc);

        status.Age.ShouldNotBeNull();
        status.Age.Value.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
        status.Age.Value.ShouldBeLessThan(TimeSpan.FromMinutes(1));
        status.ExpiresIn.ShouldBe(status.Retention - status.Age);
    }

    [Fact]
    public async Task VersionTooOld_CanReadChangesDecides()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        var tableName = await CreateTableAsync(connection);
        var oldVersion = await CurrentVersionAsync(connection);

        await connection.ExecuteAsync($"UPDATE {tableName} SET Name = 'B'; ALTER TABLE {tableName} DISABLE CHANGE_TRACKING; ALTER TABLE {tableName} ENABLE CHANGE_TRACKING;");

        var status = await new SqlChangeTrackingHelper(tableName).GetVersionStatusAsync(connection, oldVersion, cancellationToken: Token);

        // The version is recent, so ExpiresIn looks fine, but the changes can't be read since Change Tracking was enabled again
        status.CanReadChanges.ShouldBeFalse();
        status.MinValidVersion.ShouldBeGreaterThan(oldVersion);
        status.ExpiresIn!.Value.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task VersionBeforeAnyCommit_HasNoCommitTime()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        var tableName = await CreateTableAsync(connection);

        var status = await new SqlChangeTrackingHelper(tableName).GetVersionStatusAsync(connection, 0, cancellationToken: Token);

        status.CommittedAt.ShouldBeNull();
        status.Age.ShouldBeNull();
        status.ExpiresIn.ShouldBeNull();
        status.CanReadChanges.ShouldBeFalse();
    }

    [Fact]
    public async Task WithoutPermissionToTheCommitTable_Throws()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        var tableName = await CreateTableAsync(connection);
        var login = $"User_{Guid.NewGuid():N}";
        const string password = "Test_Passw0rd!";

        // Can read the changes, but not sys.dm_tran_commit_table, which then is empty instead of giving an error
        await connection.ExecuteAsync($"""
            CREATE LOGIN {login} WITH PASSWORD = '{password}';
            CREATE USER {login} FOR LOGIN {login};
            GRANT SELECT, VIEW CHANGE TRACKING ON {tableName} TO {login};
            """);

        var userConnectionString = new SqlConnectionStringBuilder(_connectionString) { UserID = login, Password = password }.ConnectionString;
        await using var userConnection = new SqlConnection(userConnectionString);
        var helper = new SqlChangeTrackingHelper(tableName);

        // Reading changes works
        await using (await helper.ReadChangesAsync(userConnection, null, cancellationToken: Token))
        {
        }

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await helper.GetVersionStatusAsync(userConnection, await CurrentVersionAsync(connection), cancellationToken: Token));

        exception.Message.ShouldContain("VIEW SERVER STATE");
        userConnection.State.ShouldBe(ConnectionState.Closed);
    }

    [Fact]
    public async Task ChangeTrackingNotEnabledForTable_Throws()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        var tableName = $"dbo.Test_{Guid.NewGuid():N}";
        await connection.ExecuteAsync($"CREATE TABLE {tableName} (Id int PRIMARY KEY);");

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await new SqlChangeTrackingHelper(tableName).GetVersionStatusAsync(connection, 1, cancellationToken: Token));

        exception.Message.ShouldContain("ENABLE CHANGE_TRACKING");
    }

    [Fact]
    public async Task TableNotFound_Throws()
    {
        await using var connection = new SqlConnection(_connectionString);

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await new SqlChangeTrackingHelper("dbo.Missing").GetVersionStatusAsync(connection, 1, cancellationToken: Token));

        exception.Message.ShouldContain("was not found");
        connection.State.ShouldBe(ConnectionState.Closed);
    }
}
