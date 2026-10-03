using System;

namespace SqlChangeTracking;

/// <summary>
/// The version to read changes since can't be used, so a full load is needed to not miss any changes.
/// It happens when the version is older than CHANGE_TRACKING_MIN_VALID_VERSION, for example when the retention period has passed,
/// or Change Tracking was disabled and enabled again. Or when it's newer than the current version, for example after a database restore.
/// Use ReinitializeWhenVersionTooOld to get a full load instead.
/// </summary>
public class ChangeTrackingVersionTooOldException : Exception
{
    /// <summary>
    /// The version the changes were requested since.
    /// </summary>
    public long SinceVersion { get; }

    /// <summary>
    /// CHANGE_TRACKING_MIN_VALID_VERSION for the table.
    /// </summary>
    public long MinValidVersion { get; }

    /// <summary>
    /// CHANGE_TRACKING_CURRENT_VERSION for the database.
    /// </summary>
    public long CurrentVersion { get; }

    /// <param name="tableName">The table the changes were requested for</param>
    /// <param name="sinceVersion">The version the changes were requested since</param>
    /// <param name="minValidVersion">CHANGE_TRACKING_MIN_VALID_VERSION for the table</param>
    /// <param name="currentVersion">CHANGE_TRACKING_CURRENT_VERSION for the database</param>
    public ChangeTrackingVersionTooOldException(string tableName, long sinceVersion, long minValidVersion, long currentVersion)
        : base($"Can't read changes for {tableName} since version {sinceVersion}, it must be between {minValidVersion} (CHANGE_TRACKING_MIN_VALID_VERSION) " +
               $"and {currentVersion} (CHANGE_TRACKING_CURRENT_VERSION). A full load is needed, read with sinceVersion null or use ReinitializeWhenVersionTooOld.")
    {
        SinceVersion = sinceVersion;
        MinValidVersion = minValidVersion;
        CurrentVersion = currentVersion;
    }
}
