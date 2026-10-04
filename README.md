# SqlChangeTrackingHelper
Read changes from SQL Server [Change Tracking](https://learn.microsoft.com/en-us/sql/relational-databases/track-changes/about-change-tracking-sql-server) the right way, without having to know all the pitfalls.

## Installing

SqlChangeTrackingHelper targets .NET 10 and uses [Microsoft.Data.SqlClient](https://www.nuget.org/packages/Microsoft.Data.SqlClient).

```
dotnet add package SqlChangeTrackingHelper
```

## What it handles for you
- **Snapshot isolation**: the current version and the changes are read in the same `SNAPSHOT` transaction, so no change is missed. A change made at the same moment can in rare cases be returned again in the next read, so handle the rows idempotently, for example with an upsert.
- **Too old versions**: if your last synced version is older than `CHANGE_TRACKING_MIN_VALID_VERSION` (retention cleanup, truncate, Change Tracking disabled and enabled again), you are told to reinitialize, instead of silently getting incomplete changes.
- **Initial load**: a full load that records the version correctly, so changes made during the load are picked up by the next sync.
- **Deletes**: deleted rows come back with their primary key and the operation, other rows with their current values.
- **Streaming**: changes are streamed, and the connection and transaction are always cleaned up, even if you stop reading.

## Usage
The same method is used for the first full load and for the changes after that, so the code that handles the rows is the same.
```csharp
var helper = new SqlChangeTrackingHelper("dbo.Bookings");

var lastVersion = store.Get("Bookings"); // null the first time

await using (var changes = await helper.ReadChangesAsync(connection, sinceVersion: lastVersion))
{
    if (changes.IsFullLoad)
    {
        // Every row in the table is returned. Replace what you have, since deleted rows are not returned.
    }

    while (await changes.Reader.ReadAsync())
    {
        var operation = changes.Reader.GetString(0); // I, U or D, then the columns of the table
        // A deleted row (D) only has the primary key, the other columns are NULL
    }

    store.Set("Bookings", changes.Version); // When everything is processed, not before
}
```
The result keeps a `SNAPSHOT` transaction open while you read, so dispose it as soon as you are done. That also ends the transaction if you stop reading early, and closes the connection if `ReadChangesAsync` opened it.

`Reader` is an ordinary `DbDataReader`, so the rows can be passed on to for example SqlBulkCopy, a Parquet or CSV writer, or Dapper's `reader.Parse<T>()`.

### Version too old
If the version is older than `CHANGE_TRACKING_MIN_VALID_VERSION` (or newer than the current version, for example after a restore), the changes can't be trusted and `ChangeTrackingVersionTooOldException` is thrown.
To get a full load instead, use `ReinitializeWhenVersionTooOld()` and check `IsFullLoad`.
```csharp
var helper = new SqlChangeTrackingHelper("dbo.Bookings")
    .ReinitializeWhenVersionTooOld();
```

### Columns
By default you get `SYS_CHANGE_OPERATION` followed by all columns of the table.
```csharp
var helper = new SqlChangeTrackingHelper("dbo.Bookings")
    .SelectColumns("Status", "ModifiedAt") // The primary key is always included
    .IncludeChangeTableColumns(ChangeTableColumns.Version | ChangeTableColumns.Context); // After SYS_CHANGE_OPERATION, NULL in a full load
```
| ChangeTableColumns | Column | |
|---|---|---|
| `Version` | `SYS_CHANGE_VERSION` | The version of the last change to the row |
| `CreationVersion` | `SYS_CHANGE_CREATION_VERSION` | The version of the last insert of the row |
| `ChangedColumns` | `SYS_CHANGE_COLUMNS` | Which columns were updated, requires `TRACK_COLUMNS_UPDATED = ON` |
| `Context` | `SYS_CHANGE_CONTEXT` | Set by the writer with `WITH CHANGE_TRACKING_CONTEXT (@context)`, for example to recognize your own changes |

## List the tracked tables
`GetChangeTrackingInfoAsync` returns the Change Tracking settings of the database and the tables that have Change Tracking enabled.
```csharp
var info = await SqlChangeTrackingHelper.GetChangeTrackingInfoAsync(connection, includeSizes: true);

Console.WriteLine($"Version {info.CurrentVersion}, retention {info.Retention}, auto cleanup {info.AutoCleanup}, snapshot isolation {info.SnapshotIsolationAllowed}");

foreach (var table in info.Tables)
{
    Console.WriteLine($"{table.QuotedName}: key ({string.Join(", ", table.PrimaryKey)}), min valid version {table.MinValidVersion}, " +
                      $"{table.Rows} rows ({table.DataSizeMb} MB), {table.ChangeTrackingRows} tracked changes ({table.ChangeTrackingSizeMb} MB)");

    if (!table.CanReadChangesSince(lastVersion)) // Same rule as ReadChangesAsync
    {
        // Needs a full load
    }
}
```
- **Retention** is how long a sync can be down before it needs a full load.
- **BeginVersion** is when Change Tracking was enabled for the table. A recent version explains a jump in `MinValidVersion`.
- **ChangeTrackingRows / ChangeTrackingSizeMb** is the internal table where SQL Server keeps the changes. If it keeps growing although the retention is short, the cleanup doesn't keep up.
- `includeSizes` reads `sys.dm_db_partition_stats`, which requires `VIEW DATABASE STATE`. Without it only `VIEW CHANGE TRACKING` is needed, like for `ReadChangesAsync`.

## How long until a sync needs a full load
`GetVersionStatusAsync` tells how a saved version stands for a table, for example to alert before a sync that hasn't run for a while needs an expensive full load.
```csharp
var status = await new SqlChangeTrackingHelper("dbo.Bookings").GetVersionStatusAsync(connection, lastVersion);

if (!status.CanReadChanges)
{
    // Too late, the next sync is a full load
}
else if (status.ExpiresIn < TimeSpan.FromHours(12))
{
    // Warn: the version is status.Age old, and the changes are only kept for status.Retention
}
```
- `CommittedAt` is when the version was committed, in UTC, and `Age` is how long ago, measured by the database clock.
- `ExpiresIn` is `Retention` minus `Age`. It's an estimate: the cleanup runs in the background and can be behind, so `ExpiresIn` can be negative while `CanReadChanges` is still true. `CanReadChanges` is what decides, with the same rule as `ReadChangesAsync`.
- The commit time comes from `sys.dm_tran_commit_table`, which requires `VIEW SERVER STATE` on SQL Server, and `VIEW DATABASE STATE` on Azure SQL Database. Without it SQL Server returns no rows instead of an error, so `GetVersionStatusAsync` throws when it can't see the commit table.

## Requirements
- `ALTER DATABASE ... SET ALLOW_SNAPSHOT_ISOLATION ON`
- `ALTER DATABASE ... SET CHANGE_TRACKING = ON` and `ALTER TABLE ... ENABLE CHANGE_TRACKING`
- The table must have a primary key.
- The user needs `VIEW CHANGE TRACKING` on the table or schema.
- Read from the primary. SQL Server doesn't support Change Tracking on secondary replicas, so it doesn't work with `ApplicationIntent=ReadOnly`.
  Read the full load from the primary too, even if it's tempting to put that load on a replica: the version must come from the same snapshot as the rows, otherwise changes that the replica hasn't received yet are lost.
