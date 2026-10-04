using System;
using System.Collections.Generic;

namespace SqlChangeTracking;

/// <summary>
/// Change Tracking settings for the database, and the tables that have Change Tracking enabled.
/// </summary>
public sealed class SqlChangeTrackingInfo
{
    internal SqlChangeTrackingInfo(string databaseName, long currentVersion, TimeSpan retention, bool autoCleanup, bool snapshotIsolationAllowed,
        IReadOnlyList<SqlChangeTrackingTableInfo> tables)
    {
        DatabaseName = databaseName;
        CurrentVersion = currentVersion;
        Retention = retention;
        AutoCleanup = autoCleanup;
        SnapshotIsolationAllowed = snapshotIsolationAllowed;
        Tables = tables;
    }

    /// <summary>
    /// The database.
    /// </summary>
    public string DatabaseName { get; }

    /// <summary>
    /// CHANGE_TRACKING_CURRENT_VERSION().
    /// </summary>
    public long CurrentVersion { get; }

    /// <summary>
    /// How long changes are kept. A sync that hasn't run for longer than this needs a full load.
    /// </summary>
    public TimeSpan Retention { get; }

    /// <summary>
    /// If changes older than the retention are cleaned up automatically. Without it the Change Tracking tables grow forever.
    /// </summary>
    public bool AutoCleanup { get; }

    /// <summary>
    /// If ALLOW_SNAPSHOT_ISOLATION is ON, which ReadChangesAsync requires.
    /// </summary>
    public bool SnapshotIsolationAllowed { get; }

    /// <summary>
    /// The tables that have Change Tracking enabled, ordered by schema and name.
    /// </summary>
    public IReadOnlyList<SqlChangeTrackingTableInfo> Tables { get; }
}

/// <summary>
/// A table that has Change Tracking enabled.
/// </summary>
public sealed class SqlChangeTrackingTableInfo
{
    private readonly long _currentVersion;

    internal SqlChangeTrackingTableInfo(string schemaName, string tableName, IReadOnlyList<string> primaryKey, long beginVersion, long minValidVersion,
        bool trackColumnsUpdated, long currentVersion, long? rows, decimal? dataSizeMb, long? changeTrackingRows, decimal? changeTrackingSizeMb)
    {
        SchemaName = schemaName;
        TableName = tableName;
        PrimaryKey = primaryKey;
        BeginVersion = beginVersion;
        MinValidVersion = minValidVersion;
        TrackColumnsUpdated = trackColumnsUpdated;
        _currentVersion = currentVersion;
        Rows = rows;
        DataSizeMb = dataSizeMb;
        ChangeTrackingRows = changeTrackingRows;
        ChangeTrackingSizeMb = changeTrackingSizeMb;
    }

    /// <summary>
    /// The schema of the table.
    /// </summary>
    public string SchemaName { get; }

    /// <summary>
    /// The name of the table.
    /// </summary>
    public string TableName { get; }

    /// <summary>
    /// Schema and table name quoted, like [dbo].[Bookings]. Can be passed to SqlChangeTrackingHelper.
    /// </summary>
    public string QuotedName => SqlChangeTrackingHelper.Quote(SchemaName) + "." + SqlChangeTrackingHelper.Quote(TableName);

    /// <summary>
    /// The primary key columns, in key order.
    /// </summary>
    public IReadOnlyList<string> PrimaryKey { get; }

    /// <summary>
    /// The version when Change Tracking was enabled for the table. A recent version means it was enabled, or disabled and enabled again, recently.
    /// </summary>
    public long BeginVersion { get; }

    /// <summary>
    /// CHANGE_TRACKING_MIN_VALID_VERSION for the table. Changes can only be read since this version or later.
    /// </summary>
    public long MinValidVersion { get; }

    /// <summary>
    /// If TRACK_COLUMNS_UPDATED is ON, so ChangeTableColumns.ChangedColumns has a value for updates.
    /// </summary>
    public bool TrackColumnsUpdated { get; }

    /// <summary>
    /// Number of rows in the table. Only with includeSizes.
    /// </summary>
    public long? Rows { get; }

    /// <summary>
    /// Reserved size of the table data (heap or clustered index, not other indexes) in MB. Only with includeSizes.
    /// </summary>
    public decimal? DataSizeMb { get; }

    /// <summary>
    /// Number of rows in the internal Change Tracking table, which keeps the changes until they are cleaned up. Only with includeSizes.
    /// </summary>
    public long? ChangeTrackingRows { get; }

    /// <summary>
    /// Reserved size of the internal Change Tracking table in MB. If it keeps growing, the cleanup doesn't keep up. Only with includeSizes.
    /// </summary>
    public decimal? ChangeTrackingSizeMb { get; }

    /// <summary>
    /// If ReadChangesAsync can read the changes since the version, or if it would need a full load.
    /// Uses the same rule as ReadChangesAsync, at the time the info was read.
    /// </summary>
    /// <param name="version">The version from the previous sync</param>
    public bool CanReadChangesSince(long version) => SqlChangeTrackingHelper.IsValidSinceVersion(version, MinValidVersion, _currentVersion);
}
