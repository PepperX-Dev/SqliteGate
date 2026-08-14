using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace PepperX.SqliteGate.Tests.Support;

/// <summary>Collects log entries so tests can assert on what the gate reported.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider, ILoggerFactory
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    /// <summary>Everything logged so far, oldest first.</summary>
    internal IReadOnlyList<LogEntry> Entries => [.. _entries];

    /// <summary>Counts entries with a given event id — the stable way to identify a message.</summary>
    /// <param name="eventId">Event id to count.</param>
    /// <returns>How many entries carried it.</returns>
    internal int Count(int eventId) => _entries.Count(entry => entry.EventId == eventId);

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider)
    {
        // Single-provider factory: nothing to add.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release.
    }

    /// <summary>One captured log entry.</summary>
    /// <param name="Level">Severity.</param>
    /// <param name="EventId">Numeric event id.</param>
    /// <param name="Message">Formatted message.</param>
    internal sealed record LogEntry(LogLevel Level, int EventId, string Message);

    private sealed class CapturingLogger(ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => entries.Enqueue(new LogEntry(logLevel, eventId.Id, formatter(state, exception)));
    }
}

/// <summary>Event ids the gate logs with, mirrored here so assertions read clearly.</summary>
internal static class GateEvents
{
    internal const int Bootstrapped = 1000;
    internal const int WriteAheadLoggingUnavailable = 1001;
    internal const int WriteQueued = 1002;
    internal const int RetryingAfterContention = 1003;
    internal const int RetriesExhausted = 1004;
}
