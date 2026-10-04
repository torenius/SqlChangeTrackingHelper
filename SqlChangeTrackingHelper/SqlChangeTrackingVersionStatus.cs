using System;

namespace SqlChangeTracking;

/// <summary>
/// How a version stands for a table: if the changes since it can still be read, how old it is and roughly how long until it can't be used.
/// </summary>
public sealed class SqlChangeTrackingVersionStatus
{
    internal SqlChangeTrackingVersionStatus(long version, long minValidVersion, long currentVersion, TimeSpan retention, DateTime? committedAt, TimeSpan? age)
    {
        Version = version;
        MinValidVersion = minValidVersion;
        CurrentVersion = currentVersion;
        Retention = retention;
        CommittedAt = committedAt;
        Age = age;
    }

    /// <summary>
    /// The version the status is for.
    /// </summary>
    public long Version { get; }

    /// <summary>
    /// CHANGE_TRACKING_MIN_VALID_VERSION for the table.
    /// </summary>
    public long MinValidVersion { get; }

    /// <summary>
    /// CHANGE_TRACKING_CURRENT_VERSION for the database.
    /// </summary>
    public long CurrentVersion { get; }

    /// <summary>
    /// If ReadChangesAsync can read the changes since the version, or if it would need a full load. This is what decides, not ExpiresIn.
    /// </summary>
    public bool CanReadChanges => SqlChangeTrackingHelper.IsValidSinceVersion(Version, MinValidVersion, CurrentVersion);

    /// <summary>
    /// How long changes are kept in the database.
    /// </summary>
    public TimeSpan Retention { get; }

    /// <summary>
    /// When the version was committed, in UTC. Null if it's no longer in the commit table, which is cleaned up like the changes,
    /// or if it isn't a version from SQL Server, like the Version of a result or CHANGE_TRACKING_CURRENT_VERSION().
    /// </summary>
    public DateTime? CommittedAt { get; }

    /// <summary>
    /// How long ago the version was committed, measured by the database clock. Null if CommittedAt is null.
    /// </summary>
    public TimeSpan? Age { get; }

    /// <summary>
    /// Retention minus Age, an estimate of how long until the version is too old and a full load is needed. Null if Age is null.
    /// It can be negative while CanReadChanges is still true, since the cleanup runs in the background and can be behind.
    /// </summary>
    public TimeSpan? ExpiresIn => Retention - Age;
}
