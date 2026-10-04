using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SqlChangeTracking;

/// <summary>
/// Reads the changes of a table from SQL Server Change Tracking, or all rows for a full load.
/// The version and the rows are read in the same SNAPSHOT transaction, so no change is missed and the version can be trusted.
/// </summary>
public class SqlChangeTrackingHelper
{
    private const string OperationColumn = "SYS_CHANGE_OPERATION";

    private readonly string _tableName;
    private List<string> _selectedColumns = [];
    private ChangeTableColumns _changeTableColumns;
    private bool _reinitializeWhenVersionTooOld;

    /// <param name="tableName">The table to read, in the current database. Can be a multipart name like "dbo.Table", and parts can be quoted like "[dbo].[My.Table]".</param>
    /// <exception cref="ArgumentNullException">If the table name is null or empty</exception>
    public SqlChangeTrackingHelper(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName)) throw new ArgumentNullException(nameof(tableName));
        _tableName = tableName;
    }

    /// <summary>
    /// Only returns these columns of the table. The primary key is always returned. Default is all columns.
    /// Calling it again replaces the previous columns.
    /// </summary>
    /// <param name="columnNames">Column names in the table</param>
    /// <returns>The SqlChangeTrackingHelper so you can continue with the builder pattern</returns>
    public SqlChangeTrackingHelper SelectColumns(params string[] columnNames)
    {
        ArgumentNullException.ThrowIfNull(columnNames);
        if (columnNames.Length == 0) throw new ArgumentException("At least one column is required", nameof(columnNames));
        if (columnNames.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Column names can't be null or empty", nameof(columnNames));

        _selectedColumns = columnNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return this;
    }

    /// <summary>
    /// Includes extra columns from CHANGETABLE, after SYS_CHANGE_OPERATION. In a full load they are NULL.
    /// </summary>
    /// <param name="columns">The columns to include, combined with |</param>
    /// <returns>The SqlChangeTrackingHelper so you can continue with the builder pattern</returns>
    public SqlChangeTrackingHelper IncludeChangeTableColumns(ChangeTableColumns columns)
    {
        _changeTableColumns = columns;
        return this;
    }

    /// <summary>
    /// When the version is too old (or newer than the current version), return a full load with IsFullLoad = true,
    /// instead of throwing ChangeTrackingVersionTooOldException.
    /// Make sure that you check IsFullLoad and replace everything you have, otherwise deleted rows are kept.
    /// </summary>
    /// <returns>The SqlChangeTrackingHelper so you can continue with the builder pattern</returns>
    public SqlChangeTrackingHelper ReinitializeWhenVersionTooOld()
    {
        _reinitializeWhenVersionTooOld = true;
        return this;
    }

    /// <summary>
    /// Reads the changes since a version, or all rows if sinceVersion is null.
    /// A SNAPSHOT transaction is started on the connection and kept open until the result is disposed, so dispose it as soon as you are done.
    /// Requires ALLOW_SNAPSHOT_ISOLATION ON for the database, Change Tracking enabled for the table, and a primary key.
    /// </summary>
    /// <param name="connection">SqlConnection to read from. If it's closed, it's opened and then closed when the result is disposed. It can't have an ongoing transaction.</param>
    /// <param name="sinceVersion">The Version from the previous result, or null for a full load</param>
    /// <param name="timeout">Number of seconds for each command to complete before it times out. 0 equals no timeout. Default 30 seconds</param>
    /// <param name="cancellationToken">Cancels the operation</param>
    /// <returns>The changes, that must be disposed</returns>
    /// <exception cref="ChangeTrackingVersionTooOldException">If the version is too old or too new and ReinitializeWhenVersionTooOld is not used</exception>
    /// <exception cref="InvalidOperationException">If the table doesn't exist, has no primary key, Change Tracking is not enabled or a selected column doesn't exist</exception>
    public async ValueTask<SqlChangeTrackingChanges> ReadChangesAsync(SqlConnection connection, long? sinceVersion, int timeout = 30, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();

        var closeConnection = false;
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            closeConnection = true;
        }

        SqlTransaction? transaction = null;
        SqlCommand? command = null;
        SqlDataReader? reader = null;
        try
        {
            transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Snapshot, cancellationToken).ConfigureAwait(false);

            // The version is read before the rows. If the snapshot starts later, a change in between is both in the rows and in the next changes, but never lost.
            var table = await GetTableInfoAsync(connection, transaction, timeout, cancellationToken).ConfigureAwait(false);

            var isFullLoad = sinceVersion is null;
            if (sinceVersion is not null && !IsValidSinceVersion(sinceVersion.Value, table.MinValidVersion, table.CurrentVersion))
            {
                if (!_reinitializeWhenVersionTooOld)
                {
                    throw new ChangeTrackingVersionTooOldException(table.QuotedName, sinceVersion.Value, table.MinValidVersion, table.CurrentVersion);
                }

                isFullLoad = true;
            }

            command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = timeout;
            command.CommandText = isFullLoad ? CreateFullLoadSql(table) : CreateChangesSql(table);
            if (!isFullLoad)
            {
                command.Parameters.Add(new SqlParameter("@SinceVersion", SqlDbType.BigInt) { Value = sinceVersion!.Value });
            }

            reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            return new SqlChangeTrackingChanges(connection, transaction, command, reader, closeConnection, table.CurrentVersion, isFullLoad ? null : sinceVersion, isFullLoad);
        }
        catch
        {
            await SqlChangeTrackingChanges.CleanUpAsync(connection, transaction, command, reader, closeConnection).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Changes can only be read since a version from CHANGE_TRACKING_MIN_VALID_VERSION up to the current version.
    /// A newer version than the current one, for example after a database restore, can't be trusted either.
    /// </summary>
    internal static bool IsValidSinceVersion(long sinceVersion, long minValidVersion, long currentVersion) =>
        sinceVersion >= minValidVersion && sinceVersion <= currentVersion;

    /// <summary>
    /// Gets the Change Tracking settings of the database and the tables that have Change Tracking enabled.
    /// Requires VIEW CHANGE TRACKING on the tables to see them, and VIEW DATABASE STATE for includeSizes.
    /// </summary>
    /// <param name="connection">SqlConnection to the database. If it's closed, it's opened and closed again.</param>
    /// <param name="includeSizes">Also gets the number of rows and the size of each table and its internal Change Tracking table. Requires VIEW DATABASE STATE.</param>
    /// <param name="timeout">Number of seconds for the command to complete before it times out. 0 equals no timeout. Default 30 seconds</param>
    /// <param name="cancellationToken">Cancels the operation</param>
    /// <returns>The settings and the tables</returns>
    /// <exception cref="InvalidOperationException">If Change Tracking is not enabled for the database</exception>
    public static async Task<SqlChangeTrackingInfo> GetChangeTrackingInfoAsync(SqlConnection connection, bool includeSizes = false, int timeout = 30,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();

        var closeConnection = false;
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            closeConnection = true;
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = timeout;
            command.CommandText = CreateChangeTrackingInfoSql(includeSizes);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Change Tracking is not enabled for the database '{connection.Database}'. " +
                                                    $"Enable it with: ALTER DATABASE {Quote(connection.Database)} SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON);");
            }

            var currentVersion = reader.GetInt64(0);
            var retention = reader.GetString(2) switch
            {
                "MINUTES" => TimeSpan.FromMinutes(reader.GetInt32(1)),
                "HOURS" => TimeSpan.FromHours(reader.GetInt32(1)),
                _ => TimeSpan.FromDays(reader.GetInt32(1))
            };
            var autoCleanup = reader.GetBoolean(3);
            var snapshotIsolationAllowed = reader.GetBoolean(4);

            // The primary key columns of all tracked tables, in key order
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            var primaryKeys = new Dictionary<int, List<string>>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var objectId = reader.GetInt32(0);
                if (!primaryKeys.TryGetValue(objectId, out var columns))
                {
                    primaryKeys[objectId] = columns = [];
                }

                columns.Add(reader.GetString(1));
            }

            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            var tables = new List<SqlChangeTrackingTableInfo>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add(new SqlChangeTrackingTableInfo(
                    schemaName: reader.GetString(1),
                    tableName: reader.GetString(2),
                    primaryKey: primaryKeys.GetValueOrDefault(reader.GetInt32(0)) ?? [],
                    beginVersion: reader.GetInt64(3),
                    minValidVersion: reader.GetInt64(4),
                    trackColumnsUpdated: reader.GetBoolean(5),
                    currentVersion: currentVersion,
                    rows: includeSizes ? GetInt64OrZero(reader, 6) : null,
                    dataSizeMb: includeSizes ? GetDecimalOrZero(reader, 7) : null,
                    changeTrackingRows: includeSizes ? GetInt64OrZero(reader, 8) : null,
                    changeTrackingSizeMb: includeSizes ? GetDecimalOrZero(reader, 9) : null));
            }

            return new SqlChangeTrackingInfo(connection.Database, currentVersion, retention, autoCleanup, snapshotIsolationAllowed, tables);
        }
        finally
        {
            if (closeConnection)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static long GetInt64OrZero(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? 0 : reader.GetInt64(ordinal);

    private static decimal GetDecimalOrZero(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? 0 : reader.GetDecimal(ordinal);

    private static string CreateChangeTrackingInfoSql(bool includeSizes)
    {
        const string database = """
            SELECT
                CHANGE_TRACKING_CURRENT_VERSION(),
                CTD.retention_period,
                CTD.retention_period_units_desc,
                CAST(CTD.is_auto_cleanup_on AS bit),
                CAST(CASE WHEN D.snapshot_isolation_state = 1 THEN 1 ELSE 0 END AS bit)
            FROM sys.change_tracking_databases CTD
            JOIN sys.databases D ON D.database_id = CTD.database_id
            WHERE CTD.database_id = DB_ID();

            SELECT I.object_id, C.name
            FROM sys.change_tracking_tables CTT
            JOIN sys.indexes I ON I.object_id = CTT.object_id AND I.is_primary_key = 1
            JOIN sys.index_columns IC ON IC.object_id = I.object_id AND IC.index_id = I.index_id
            JOIN sys.columns C ON C.object_id = IC.object_id AND C.column_id = IC.column_id
            ORDER BY I.object_id, IC.key_ordinal;

            """;

        // Each size is summed in its own APPLY, joining both before summing would multiply the rows of partitioned tables.
        // Index 0 is a heap and 1 a clustered index, so other indexes are not counted. A page is 8 KB.
        var sizes = includeSizes
            ? """
                  Data.Rows,
                  Data.SizeMb,
                  Side.Rows,
                  Side.SizeMb
              FROM sys.change_tracking_tables CTT
              JOIN sys.tables T ON T.object_id = CTT.object_id
              JOIN sys.schemas S ON S.schema_id = T.schema_id
              OUTER APPLY (
                  SELECT SUM(PS.row_count) AS Rows, CAST(SUM(PS.reserved_page_count) * 8 / 1024.0 AS decimal(19, 2)) AS SizeMb
                  FROM sys.dm_db_partition_stats PS
                  WHERE PS.object_id = T.object_id AND PS.index_id IN (0, 1)
              ) Data
              OUTER APPLY (
                  SELECT SUM(PS.row_count) AS Rows, CAST(SUM(PS.reserved_page_count) * 8 / 1024.0 AS decimal(19, 2)) AS SizeMb
                  FROM sys.internal_tables IT
                  JOIN sys.dm_db_partition_stats PS ON PS.object_id = IT.object_id AND PS.index_id IN (0, 1)
                  WHERE IT.parent_object_id = T.object_id AND IT.internal_type = 209 -- Change Tracking
              ) Side
              """
            : """
                  CAST(NULL AS bigint),
                  CAST(NULL AS decimal(19, 2)),
                  CAST(NULL AS bigint),
                  CAST(NULL AS decimal(19, 2))
              FROM sys.change_tracking_tables CTT
              JOIN sys.tables T ON T.object_id = CTT.object_id
              JOIN sys.schemas S ON S.schema_id = T.schema_id
              """;

        // CHANGE_TRACKING_MIN_VALID_VERSION, like ReadChangesAsync, so CanReadChangesSince gives the same answer
        return database + """
            SELECT
                T.object_id,
                S.name,
                T.name,
                CTT.begin_version,
                CHANGE_TRACKING_MIN_VALID_VERSION(T.object_id),
                CAST(CTT.is_track_columns_updated_on AS bit),

            """ + sizes + """

            ORDER BY S.name, T.name;
            """;
    }

    private sealed record TableInfo(string QuotedName, long MinValidVersion, long CurrentVersion, List<string> PrimaryKey, List<string> Columns);

    private async Task<TableInfo> GetTableInfoAsync(SqlConnection connection, SqlTransaction transaction, int timeout, CancellationToken cancellationToken)
    {
        // OBJECT_ID parses the multipart name, so the name is never concatenated into SQL
        const string sql = """
            DECLARE @ObjectId int = (SELECT object_id FROM sys.tables WHERE object_id = OBJECT_ID(@TableName));

            SELECT
                SCHEMA_NAME(T.schema_id),
                T.name,
                CHANGE_TRACKING_MIN_VALID_VERSION(@ObjectId),
                CHANGE_TRACKING_CURRENT_VERSION()
            FROM sys.tables T
            WHERE T.object_id = @ObjectId;

            SELECT C.name
            FROM sys.indexes I
            JOIN sys.index_columns IC ON IC.object_id = I.object_id AND IC.index_id = I.index_id
            JOIN sys.columns C ON C.object_id = IC.object_id AND C.column_id = IC.column_id
            WHERE I.object_id = @ObjectId AND I.is_primary_key = 1
            ORDER BY IC.key_ordinal;

            SELECT C.name
            FROM sys.columns C
            WHERE C.object_id = @ObjectId
            ORDER BY C.column_id;
            """;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = timeout;
        command.CommandText = sql;
        command.Parameters.Add(new SqlParameter("@TableName", SqlDbType.NVarChar, 4000) { Value = _tableName });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"The table '{_tableName}' was not found in the database '{connection.Database}'.");
        }

        var quotedName = Quote(reader.GetString(0)) + "." + Quote(reader.GetString(1));
        if (await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Change Tracking is not enabled for {quotedName}. Enable it with: ALTER TABLE {quotedName} ENABLE CHANGE_TRACKING;");
        }

        var minValidVersion = reader.GetInt64(2);
        var currentVersion = reader.GetInt64(3);

        var primaryKey = await ReadNamesAsync(reader, cancellationToken).ConfigureAwait(false);
        var columns = await ReadNamesAsync(reader, cancellationToken).ConfigureAwait(false);

        if (primaryKey.Count == 0)
        {
            // Can't happen, since Change Tracking requires a primary key, but a clear message is better than invalid SQL
            throw new InvalidOperationException($"{quotedName} has no primary key, which Change Tracking requires.");
        }

        return new TableInfo(quotedName, minValidVersion, currentVersion, primaryKey, SelectColumns(quotedName, primaryKey, columns));
    }

    private static async Task<List<string>> ReadNamesAsync(SqlDataReader reader, CancellationToken cancellationToken)
    {
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);

        var names = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>
    /// The columns to return, in the order of the table. The primary key is always included.
    /// </summary>
    private List<string> SelectColumns(string quotedName, List<string> primaryKey, List<string> columns)
    {
        if (_selectedColumns.Count == 0)
        {
            return columns;
        }

        var missing = _selectedColumns.Where(x => !columns.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"{quotedName} has no column {string.Join(", ", missing.Select(x => $"'{x}'"))}.");
        }

        return columns
            .Where(x => primaryKey.Contains(x) || _selectedColumns.Contains(x, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private string CreateFullLoadSql(TableInfo table)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SELECT");
        sb.Append("\tCAST(N'I' AS nchar(1)) AS ").Append(OperationColumn);

        foreach (var (name, type) in GetChangeTableColumns())
        {
            sb.AppendLine(",").Append("\tCAST(NULL AS ").Append(type).Append(") AS ").Append(name);
        }

        foreach (var column in table.Columns)
        {
            sb.AppendLine(",").Append("\tT.").Append(Quote(column));
        }

        sb.AppendLine().Append("FROM ").Append(table.QuotedName).AppendLine(" AS T;");
        return sb.ToString();
    }

    private string CreateChangesSql(TableInfo table)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SELECT");
        sb.Append("\tC.").Append(OperationColumn);

        foreach (var (name, _) in GetChangeTableColumns())
        {
            sb.AppendLine(",").Append("\tC.").Append(name);
        }

        // The primary key comes from CHANGETABLE, so deleted rows have it too
        foreach (var column in table.Columns)
        {
            sb.AppendLine(",").Append(table.PrimaryKey.Contains(column) ? "\tC." : "\tT.").Append(Quote(column));
        }

        sb.AppendLine().Append("FROM CHANGETABLE(CHANGES ").Append(table.QuotedName).AppendLine(", @SinceVersion) AS C");
        sb.Append("LEFT JOIN ").Append(table.QuotedName).Append(" AS T ON ")
            .AppendJoin(" AND ", table.PrimaryKey.Select(x => $"T.{Quote(x)} = C.{Quote(x)}")).AppendLine(";");

        return sb.ToString();
    }

    /// <summary>
    /// The selected CHANGETABLE columns with their types, which are used for the NULLs in a full load.
    /// </summary>
    private IEnumerable<(string Name, string Type)> GetChangeTableColumns()
    {
        if (_changeTableColumns.HasFlag(ChangeTableColumns.Version)) yield return ("SYS_CHANGE_VERSION", "bigint");
        if (_changeTableColumns.HasFlag(ChangeTableColumns.CreationVersion)) yield return ("SYS_CHANGE_CREATION_VERSION", "bigint");
        if (_changeTableColumns.HasFlag(ChangeTableColumns.ChangedColumns)) yield return ("SYS_CHANGE_COLUMNS", "varbinary(4100)");
        if (_changeTableColumns.HasFlag(ChangeTableColumns.Context)) yield return ("SYS_CHANGE_CONTEXT", "varbinary(128)");
    }

    internal static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";
}
