using System;

namespace SqlChangeTracking;

/// <summary>
/// Extra columns from CHANGETABLE to include in the result, after SYS_CHANGE_OPERATION.
/// SYS_CHANGE_OPERATION and the primary key are always included. In a full load the extra columns are NULL.
/// </summary>
[Flags]
public enum ChangeTableColumns
{
    /// <summary>
    /// Only SYS_CHANGE_OPERATION and the primary key.
    /// </summary>
    None = 0,

    /// <summary>
    /// SYS_CHANGE_VERSION (bigint): the version of the last change to the row.
    /// </summary>
    Version = 1,

    /// <summary>
    /// SYS_CHANGE_CREATION_VERSION (bigint): the version of the last insert of the row.
    /// </summary>
    CreationVersion = 2,

    /// <summary>
    /// SYS_CHANGE_COLUMNS (varbinary): which columns were updated. Requires TRACK_COLUMNS_UPDATED = ON on the table, and is NULL for inserts and deletes.
    /// Use CHANGE_TRACKING_IS_COLUMN_IN_MASK to check a column.
    /// </summary>
    ChangedColumns = 4,

    /// <summary>
    /// SYS_CHANGE_CONTEXT (varbinary): the context the change was made with, by WITH CHANGE_TRACKING_CONTEXT (@context).
    /// Can be used to recognize your own changes, for example when syncing in both directions.
    /// </summary>
    Context = 8
}
