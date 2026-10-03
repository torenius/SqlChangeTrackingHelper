using Dapper;
using Microsoft.Data.SqlClient;
using Shouldly;

namespace SqlChangeTracking.Tests;

/// <summary>
/// Verifies that the test database is set up for Change Tracking, so other tests fail for the right reason.
/// </summary>
public class EnvironmentTests(MsSqlFixture fixture) : IClassFixture<MsSqlFixture>
{
    [Fact]
    public async Task ChangeTracking_And_SnapshotIsolation_AreEnabled()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await connection.ExecuteAsync("""
            CREATE TABLE dbo.Environment (Id int PRIMARY KEY, Name nvarchar(10) NOT NULL);
            ALTER TABLE dbo.Environment ENABLE CHANGE_TRACKING;
            """);

        var before = await connection.ExecuteScalarAsync<long>("SELECT CHANGE_TRACKING_CURRENT_VERSION();");
        await connection.ExecuteAsync("INSERT INTO dbo.Environment (Id, Name) VALUES (1, 'A');");

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Snapshot, TestContext.Current.CancellationToken);
        var changes = (await connection.QueryAsync<(string Operation, int Id)>(
            "SELECT SYS_CHANGE_OPERATION, Id FROM CHANGETABLE(CHANGES dbo.Environment, @before) AS C;", new { before }, transaction)).ToList();

        changes.ShouldBe([("I", 1)]);
    }
}
