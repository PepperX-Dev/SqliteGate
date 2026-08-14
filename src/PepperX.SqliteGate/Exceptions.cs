namespace PepperX.SqliteGate;

/// <summary>
/// Raised when the gate itself cannot do its job — a database that refuses write-ahead logging, or
/// a gate used after disposal. Errors coming from SQLite are surfaced unchanged as
/// <see cref="Microsoft.Data.Sqlite.SqliteException"/> so existing handling keeps working.
/// </summary>
public class SqliteGateException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">Description of what went wrong.</param>
    public SqliteGateException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the error underneath it.</summary>
    /// <param name="message">Description of what went wrong.</param>
    /// <param name="innerException">The underlying error.</param>
    public SqliteGateException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when a write waited longer than <see cref="SqliteGateOptions.WriteQueueTimeout"/> for its
/// turn in the queue. It means the queue is saturated, not that SQLite was busy — nothing was ever
/// sent to the database.
/// </summary>
public sealed class SqliteGateTimeoutException : SqliteGateException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="databasePath">Database whose write queue the caller was waiting on.</param>
    /// <param name="timeout">The configured wait limit that elapsed.</param>
    public SqliteGateTimeoutException(string databasePath, TimeSpan timeout)
        : base($"Timed out after {timeout.TotalMilliseconds:N0} ms waiting for the write queue of '{databasePath}'. " +
               "Either writes are taking longer than expected, or WriteQueueTimeout is set too low for the load.")
    {
        DatabasePath = databasePath;
        Timeout = timeout;
    }

    /// <summary>Database whose write queue the caller was waiting on.</summary>
    public string DatabasePath { get; }

    /// <summary>The configured wait limit that elapsed.</summary>
    public TimeSpan Timeout { get; }
}
