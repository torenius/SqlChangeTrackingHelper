using System;
using System.Data.Common;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SqlChangeTracking;

/// <summary>
/// The changes from ReadChangesAsync. The rows are read with Reader, while a snapshot transaction is kept open.
/// Dispose it as soon as you are done, also if you stop reading before the end. That ends the transaction and closes the connection if it was opened by ReadChangesAsync.
/// </summary>
public sealed class SqlChangeTrackingChanges : IAsyncDisposable
{
    private readonly SqlConnection _connection;
    private readonly SqlTransaction _transaction;
    private readonly SqlCommand _command;
    private readonly SqlDataReader _reader;
    private readonly bool _closeConnection;
    private bool _disposed;

    internal SqlChangeTrackingChanges(SqlConnection connection, SqlTransaction transaction, SqlCommand command, SqlDataReader reader, bool closeConnection,
        long version, long? sinceVersion, bool isFullLoad)
    {
        _connection = connection;
        _transaction = transaction;
        _command = command;
        _reader = reader;
        _closeConnection = closeConnection;
        Version = version;
        SinceVersion = sinceVersion;
        IsFullLoad = isFullLoad;
    }

    /// <summary>
    /// The rows. The first column is SYS_CHANGE_OPERATION (I, U or D), then the CHANGETABLE columns from IncludeChangeTableColumns, then the columns of the table.
    /// For a deleted row only the primary key has a value, the other columns of the table are NULL.
    /// In a full load every row has the operation I.
    /// </summary>
    public DbDataReader Reader => _reader;

    /// <summary>
    /// The version to read the next changes since. Save it when all rows are processed, not before, so nothing is lost if the processing fails.
    /// </summary>
    public long Version { get; }

    /// <summary>
    /// The version the changes were read since, or null for a full load that was requested.
    /// </summary>
    public long? SinceVersion { get; }

    /// <summary>
    /// True if every row in the table is returned, either because no version was given or because of ReinitializeWhenVersionTooOld.
    /// Then the rows should replace everything you have, since rows that are deleted are not returned.
    /// </summary>
    public bool IsFullLoad { get; }

    /// <summary>
    /// Ends the read and the transaction, and closes the connection if it was opened by ReadChangesAsync.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await CleanUpAsync(_connection, _transaction, _command, _reader, _closeConnection).ConfigureAwait(false);
    }

    /// <summary>
    /// Best effort, so a failure doesn't hide an original exception or leave the connection open.
    /// </summary>
    internal static async ValueTask CleanUpAsync(SqlConnection connection, SqlTransaction? transaction, SqlCommand? command, SqlDataReader? reader, bool closeConnection)
    {
        try
        {
            if (reader is not null)
            {
                // Without Cancel, disposing the reader would read and discard all remaining rows. Nothing happens if all rows are read.
                command?.Cancel();
                await reader.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Ignore, the transaction and connection are cleaned up below
        }

        try
        {
            if (command is not null)
            {
                await command.DisposeAsync().ConfigureAwait(false);
            }

            if (transaction is not null)
            {
                // Nothing has been written, so a rollback is the same as a commit
                await transaction.RollbackAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Ignore, for example if the transaction is already ended by the server
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }

            if (closeConnection)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }
}
