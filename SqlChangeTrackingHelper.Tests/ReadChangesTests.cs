using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Data.SqlClient;
using Shouldly;

namespace SqlChangeTracking.Tests;

public class ReadChangesTests(MsSqlFixture fixture) : IClassFixture<MsSqlFixture>
{
    private readonly string _connectionString = fixture.ConnectionString;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Creates a table with Change Tracking and a few rows. Every test gets its own table, since they share the database.
    /// </summary>
    private async Task<string> CreateTableAsync(SqlConnection connection, int rows = 3, string? name = null, bool trackColumnsUpdated = false)
    {
        var tableName = name ?? $"dbo.Test_{Guid.NewGuid():N}";
        await connection.ExecuteAsync($"""
            CREATE TABLE {tableName} (Id int PRIMARY KEY, Name nvarchar(20) NULL, Price decimal(18, 2) NOT NULL);
            ALTER TABLE {tableName} ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = {(trackColumnsUpdated ? "ON" : "OFF")});
            """);

        if (rows > 0)
        {
            await connection.ExecuteAsync($"INSERT INTO {tableName} (Id, Name, Price) VALUES (@Id, @Name, @Price)",
                Enumerable.Range(1, rows).Select(x => new { Id = x, Name = "Name " + x, Price = x * 1.5m }));
        }

        return tableName;
    }

    private static async Task<List<Dictionary<string, object?>>> ReadAllAsync(SqlChangeTrackingChanges changes)
    {
        var rows = new List<Dictionary<string, object?>>();
        while (await changes.Reader.ReadAsync(Token))
        {
            rows.Add(Enumerable.Range(0, changes.Reader.FieldCount)
                .ToDictionary(changes.Reader.GetName, i => changes.Reader.IsDBNull(i) ? null : changes.Reader.GetValue(i)));
        }

        return rows;
    }

    private static List<string> ColumnNames(DbDataReader reader) => Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();

    private async Task<long> CurrentVersionAsync(SqlConnection connection) =>
        await connection.ExecuteScalarAsync<long>("SELECT CHANGE_TRACKING_CURRENT_VERSION();");

    [Fact]
    public async Task FullLoad_ReturnsAllRowsAsInserts()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        var tableName = await CreateTableAsync(connection);

        await using var changes = await new SqlChangeTrackingHelper(tableName).ReadChangesAsync(connection, sinceVersion: null, cancellationToken: Token);

        changes.IsFullLoad.ShouldBeTrue();
        changes.SinceVersion.ShouldBeNull();
        await using (var other = new SqlConnection(_connectionString))
        {
            changes.Version.ShouldBe(await CurrentVersionAsync(other));
        }

        ColumnNames(changes.Reader).ShouldBe(["SYS_CHANGE_OPERATION", "Id", "Name", "Price"]);

        var rows = await ReadAllAsync(changes);
        rows.Count.ShouldBe(3);
        rows.ShouldAllBe(x => (string)x["SYS_CHANGE_OPERATION"]! == "I");
        rows.Select(x => (int)x["Id"]!).ShouldBe([1, 2, 3], ignoreOrder: true);
    }

    [Fact]
    public async Task Changes_InsertUpdateDelete()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        var tableName = await CreateTableAsync(connection);
        var since = await CurrentVersionAsync(connection);

        await connection.ExecuteAsync($"""
            UPDATE {tableName} SET Name = 'Updated' WHERE Id = 1;
            DELETE FROM {tableName} WHERE Id = 2;
            INSERT INTO {tableName} (Id, Name, Price) VALUES (4, 'New', 4);
            UPDATE {tableName} SET Price = 40 WHERE Id = 4;
            """);

        await using var changes = await new SqlChangeTrackingHelper(tableName).ReadChangesAsync(connection, since, cancellationToken: Token);

        changes.IsFullLoad.ShouldBeFalse();
        changes.SinceVersion.ShouldBe(since);
        changes.Version.ShouldBeGreaterThan(since);

        var rows = (await ReadAllAsync(changes)).ToDictionary(x => (int)x["Id"]!);
        rows.Count.ShouldBe(3);

        rows[1]["SYS_CHANGE_OPERATION"].ShouldBe("U");
        rows[1]["Name"].ShouldBe("Updated");

        // Deleted rows only have the primary key
        rows[2]["SYS_CHANGE_OPERATION"].ShouldBe("D");
        rows[2]["Name"].ShouldBeNull();
        rows[2]["Price"].ShouldBeNull();

        // Net change: inserted and then updated is an insert with the latest values
        rows[4]["SYS_CHANGE_OPERATION"].ShouldBe("I");
        rows[4]["Price"].ShouldBe(40m);
    }

    [Fact]
    public async Task Changes_NothingChanged_ReturnsNoRowsAndSameVersion()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        var tableName = await CreateTableAsync(connection);
        var since = await CurrentVersionAsync(connection);

        await using var changes = await new SqlChangeTrackingHelper(tableName).ReadChangesAsync(connection, since, cancellationToken: Token);

        (await ReadAllAsync(changes)).ShouldBeEmpty();
        changes.Version.ShouldBe(since);
    }

    [Fact]
    public async Task FullLoad_ChangesDuringTheLoad_AreInTheNextChanges()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        var tableName = await CreateTableAsync(connection);
        var helper = new SqlChangeTrackingHelper(tableName);

        long version;
        await using (var fullLoad = await helper.ReadChangesAsync(connection, sinceVersion: null, cancellationToken: Token))
        {
            // Changed by someone else while the full load is being read
            await using (var other = new SqlConnection(_connectionString))
            {
                await other.ExecuteAsync($"""
                    UPDATE {tableName} SET Name = 'Changed during load' WHERE Id = 1;
                    DELETE FROM {tableName} WHERE Id = 2;
                    INSERT INTO {tableName} (Id, Name, Price) VALUES (99, 'Inserted during load', 1);
                    """);
            }

            // The snapshot doesn't see the changes
            var rows = (await ReadAllAsync(fullLoad)).ToDictionary(x => (int)x["Id"]!);
            rows.Keys.ShouldBe([1, 2, 3], ignoreOrder: true);
            rows[1]["Name"].ShouldBe("Name 1");

            version = fullLoad.Version;
        }

        // But they are not lost
        await using var changes = await helper.ReadChangesAsync(connection, version, cancellationToken: Token);
        var changed = (await ReadAllAsync(changes)).ToDictionary(x => (int)x["Id"]!, x => (string)x["SYS_CHANGE_OPERATION"]!);
        changed.ShouldBe(new Dictionary<int, string> { [1] = "U", [2] = "D", [99] = "I" }, ignoreOrder: true);
    }

    [Fact]
    public async Task VersionTooOld_Throws()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        var tableName = await CreateTableAsync(connection);
        var since = await CurrentVersionAsync(connection);

        // A change, so the version is bumped, and then Change Tracking is enabled again which raises CHANGE_TRACKING_MIN_VALID_VERSION
        await connection.ExecuteAsync($"UPDATE {tableName} SET Name = 'Changed' WHERE Id = 1;");
        await connection.ExecuteAsync($"ALTER TABLE {tableName} DISABLE CHANGE_TRACKING; ALTER TABLE {tableName} ENABLE CHANGE_TRACKING;");

        var exception = await Should.ThrowAsync<ChangeTrackingVersionTooOldException>(async () =>
            await new SqlChangeTrackingHelper(tableName).ReadChangesAsync(connection, since, cancellationToken: Token));

        exception.SinceVersion.ShouldBe(since);
        exception.MinValidVersion.ShouldBeGreaterThan(since);
        (await connection.ExecuteScalarAsync<int>("SELECT @@TRANCOUNT")).ShouldBe(0);
    }

    [Fact]
    public async Task VersionNewerThanCurrent_Throws()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        var tableName = await CreateTableAsync(connection);
        var current = await CurrentVersionAsync(connection);

        var exception = await Should.ThrowAsync<ChangeTrackingVersionTooOldException>(async () =>
            await new SqlChangeTrackingHelper(tableName).ReadChangesAsync(connection, current + 1000, cancellationToken: Token));

        exception.CurrentVersion.ShouldBe(current);
    }

    [Fact]
    public async Task VersionTooOld_ReinitializeWhenVersionTooOld_ReturnsFullLoad()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        var tableName = await CreateTableAsync(connection);
        var current = await CurrentVersionAsync(connection);

        await using var changes = await new SqlChangeTrackingHelper(tableName)
            .ReinitializeWhenVersionTooOld()
            .ReadChangesAsync(connection, current + 1000, cancellationToken: Token);

        changes.IsFullLoad.ShouldBeTrue();
        changes.SinceVersion.ShouldBeNull();
        (await ReadAllAsync(changes)).Count.ShouldBe(3);
    }

    [Fact]
    public async Task SelectColumns_AlwaysIncludesPrimaryKey()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        var tableName = await CreateTableAsync(connection);

        await using var changes = await new SqlChangeTrackingHelper(tableName)
            .SelectColumns("price")
            .ReadChangesAsync(connection, null, cancellationToken: Token);

        ColumnNames(changes.Reader).ShouldBe(["SYS_CHANGE_OPERATION", "Id", "Price"]);
    }

    [Fact]
    public async Task IncludeChangeTableColumns()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        var tableName = await CreateTableAsync(connection, trackColumnsUpdated: true);
        var since = await CurrentVersionAsync(connection);

        await connection.ExecuteAsync($"""
            DECLARE @Context varbinary(128) = 0x0102;
            WITH CHANGE_TRACKING_CONTEXT (@Context)
            UPDATE {tableName} SET Name = 'Changed' WHERE Id = 1;
            """);

        var helper = new SqlChangeTrackingHelper(tableName)
            .IncludeChangeTableColumns(ChangeTableColumns.Version | ChangeTableColumns.CreationVersion | ChangeTableColumns.ChangedColumns | ChangeTableColumns.Context);

        await using (var changes = await helper.ReadChangesAsync(connection, since, cancellationToken: Token))
        {
            ColumnNames(changes.Reader).ShouldBe(["SYS_CHANGE_OPERATION", "SYS_CHANGE_VERSION", "SYS_CHANGE_CREATION_VERSION", "SYS_CHANGE_COLUMNS", "SYS_CHANGE_CONTEXT", "Id", "Name", "Price"]);

            var row = (await ReadAllAsync(changes)).Single();
            row["SYS_CHANGE_VERSION"].ShouldBe(changes.Version);
            row["SYS_CHANGE_COLUMNS"].ShouldNotBeNull();
            row["SYS_CHANGE_CONTEXT"].ShouldBe(new byte[] { 1, 2 });
        }

        // Same columns and types in a full load, but NULL
        await using (var fullLoad = await helper.ReadChangesAsync(connection, null, cancellationToken: Token))
        {
            fullLoad.Reader.GetFieldType(1).ShouldBe(typeof(long));
            fullLoad.Reader.GetFieldType(3).ShouldBe(typeof(byte[]));

            var row = (await ReadAllAsync(fullLoad)).First();
            row["SYS_CHANGE_VERSION"].ShouldBeNull();
            row["SYS_CHANGE_CONTEXT"].ShouldBeNull();
        }
    }

    [Fact]
    public async Task CompositeKeyAndQuotedTableName()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        var tableName = $"[dbo].[My.Table]]_{Guid.NewGuid():N}]";
        await connection.ExecuteAsync($"""
            CREATE TABLE {tableName} (TenantId int NOT NULL, Sku varchar(10) NOT NULL, Name nvarchar(20) NULL, PRIMARY KEY (TenantId, Sku));
            ALTER TABLE {tableName} ENABLE CHANGE_TRACKING;
            INSERT INTO {tableName} VALUES (1, 'A', 'A'), (1, 'B', 'B'), (2, 'A', 'A');
            """);
        var since = await CurrentVersionAsync(connection);
        await connection.ExecuteAsync($"DELETE FROM {tableName} WHERE TenantId = 1 AND Sku = 'B'; UPDATE {tableName} SET Name = 'X' WHERE TenantId = 2;");

        await using var changes = await new SqlChangeTrackingHelper(tableName).ReadChangesAsync(connection, since, cancellationToken: Token);

        var rows = (await ReadAllAsync(changes)).ToDictionary(x => $"{x["TenantId"]}:{x["Sku"]}", x => (string)x["SYS_CHANGE_OPERATION"]!);
        rows.ShouldBe(new Dictionary<string, string> { ["1:B"] = "D", ["2:A"] = "U" }, ignoreOrder: true);
    }

    [Fact]
    public async Task ClosedConnection_IsClosedWhenDisposed()
    {
        await using var connection = new SqlConnection(_connectionString);
        string tableName;
        await using (var setup = new SqlConnection(_connectionString))
        {
            await setup.OpenAsync(Token);
            tableName = await CreateTableAsync(setup);
        }

        var changes = await new SqlChangeTrackingHelper(tableName).ReadChangesAsync(connection, null, cancellationToken: Token);
        connection.State.ShouldBe(ConnectionState.Open);

        await changes.DisposeAsync();
        connection.State.ShouldBe(ConnectionState.Closed);
    }

    [Fact]
    public async Task OpenConnection_StopReadingEarly_EndsTransactionAndKeepsConnectionOpen()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        var tableName = await CreateTableAsync(connection, rows: 0);
        await connection.ExecuteAsync($"""
            INSERT INTO {tableName} (Id, Name, Price)
            SELECT TOP (100000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), 'Name', 1 FROM sys.all_objects A CROSS JOIN sys.all_objects B;
            """, commandTimeout: 120);

        await using (var changes = await new SqlChangeTrackingHelper(tableName).ReadChangesAsync(connection, null, cancellationToken: Token))
        {
            (await changes.Reader.ReadAsync(Token)).ShouldBeTrue();
        }

        connection.State.ShouldBe(ConnectionState.Open);
        (await connection.ExecuteScalarAsync<int>("SELECT @@TRANCOUNT")).ShouldBe(0);
    }

    [Fact]
    public async Task ConnectionWithTransaction_Throws()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        var tableName = await CreateTableAsync(connection);

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(Token);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await new SqlChangeTrackingHelper(tableName).ReadChangesAsync(connection, null, cancellationToken: Token));
    }

    public static TheoryData<string, string> InvalidTables => new()
    {
        { "CREATE TABLE {0} (Id int PRIMARY KEY);", "Change Tracking is not enabled" },
        { "", "was not found" },
    };

    [Theory]
    [MemberData(nameof(InvalidTables))]
    public async Task InvalidTable_Throws(string createSql, string expectedMessage)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        var tableName = $"dbo.Test_{Guid.NewGuid():N}";
        if (createSql.Length > 0)
        {
            await connection.ExecuteAsync(string.Format(createSql, tableName));
        }

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await new SqlChangeTrackingHelper(tableName).ReadChangesAsync(connection, null, cancellationToken: Token));

        exception.Message.ShouldContain(expectedMessage);
        (await connection.ExecuteScalarAsync<int>("SELECT @@TRANCOUNT")).ShouldBe(0);
    }

    [Fact]
    public async Task SelectColumns_MissingColumn_Throws()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        var tableName = await CreateTableAsync(connection);

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await new SqlChangeTrackingHelper(tableName).SelectColumns("Name", "Missing").ReadChangesAsync(connection, null, cancellationToken: Token));

        exception.Message.ShouldContain("'Missing'");
    }

    [Fact]
    public void InvalidArguments_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new SqlChangeTrackingHelper(" "));
        Should.Throw<ArgumentException>(() => new SqlChangeTrackingHelper("dbo.Table").SelectColumns());
        Should.Throw<ArgumentException>(() => new SqlChangeTrackingHelper("dbo.Table").SelectColumns("Id", ""));
    }
}
