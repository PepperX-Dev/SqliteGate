using Microsoft.Extensions.Logging;

namespace PepperX.SqliteGate.Internals;

/// <summary>
/// Source-generated log messages. Everything the gate says is about contention, because that is the
/// only thing a caller cannot see for themselves.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Debug,
        Message = "SqliteGate bootstrapped '{Database}': journal_mode={JournalMode}, synchronous={Synchronous}, busy_timeout={BusyTimeoutMs}ms, foreign_keys={ForeignKeys}.")]
    internal static partial void Bootstrapped(
        ILogger logger,
        string database,
        string journalMode,
        string synchronous,
        int busyTimeoutMs,
        bool foreignKeys);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "SqliteGate could not enable write-ahead logging on '{Database}' (journal_mode is '{JournalMode}'). Reads will block behind writes.")]
    internal static partial void WriteAheadLoggingUnavailable(ILogger logger, string database, string journalMode);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Debug,
        Message = "Write on '{Database}' waited {WaitMs}ms behind {QueueDepth} other write(s).")]
    internal static partial void WriteQueued(ILogger logger, string database, long waitMs, int queueDepth);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "SQLite reported {ErrorCode} on '{Database}'; retrying in {DelayMs}ms (attempt {Attempt} of {MaxAttempts}).")]
    internal static partial void RetryingAfterContention(
        ILogger logger,
        string database,
        int errorCode,
        long delayMs,
        int attempt,
        int maxAttempts);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Error,
        Message = "SQLite kept reporting contention on '{Database}' after {Attempts} attempt(s) over {ElapsedMs}ms; giving up and rethrowing.")]
    internal static partial void RetriesExhausted(ILogger logger, string database, int attempts, long elapsedMs, Exception exception);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Debug,
        Message = "Reader pool for '{Database}' opened connection {Opened} of {Capacity}.")]
    internal static partial void ReaderConnectionOpened(ILogger logger, string database, int opened, int capacity);

    [LoggerMessage(
        EventId = 1006,
        Level = LogLevel.Warning,
        Message = "Discarding a faulted reader connection for '{Database}'; a replacement will be opened on demand.")]
    internal static partial void ReaderConnectionDiscarded(ILogger logger, string database, Exception exception);
}
