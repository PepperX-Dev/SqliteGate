using System.Diagnostics.Metrics;

namespace PepperX.SqliteGate.Internals;

/// <summary>
/// The <see cref="Meter"/> every gate reports to. Contention is the thing you cannot reason about
/// from a stack trace, so queue depth and wait time are first-class instruments rather than log lines.
/// </summary>
internal static class SqliteGateMetrics
{
    /// <summary>Meter name to subscribe to, for example from OpenTelemetry.</summary>
    internal const string MeterName = "PepperX.SqliteGate";

    private static readonly Meter Meter = new(MeterName, typeof(SqliteGateMetrics).Assembly.GetName().Version?.ToString() ?? "1.0.0");

    /// <summary>Writes queued or executing, per database.</summary>
    internal static readonly UpDownCounter<int> WriteQueueDepth = Meter.CreateUpDownCounter<int>(
        "sqlitegate.write_queue.depth",
        unit: "{write}",
        description: "Writes currently queued or executing for a database file.");

    /// <summary>Time a write spent waiting for the write slot.</summary>
    internal static readonly Histogram<double> WriteWaitDuration = Meter.CreateHistogram<double>(
        "sqlitegate.write.wait.duration",
        unit: "ms",
        description: "Time a write spent queued before it was granted the write slot.");

    /// <summary>Time a write spent executing once it held the slot.</summary>
    internal static readonly Histogram<double> WriteDuration = Meter.CreateHistogram<double>(
        "sqlitegate.write.duration",
        unit: "ms",
        description: "Time a write spent executing while holding the write slot.");

    /// <summary>Time a read spent executing, including the wait for a pooled connection.</summary>
    internal static readonly Histogram<double> ReadDuration = Meter.CreateHistogram<double>(
        "sqlitegate.read.duration",
        unit: "ms",
        description: "Time a read spent executing, including waiting for a pooled reader connection.");

    /// <summary>Retries caused by SQLITE_BUSY or SQLITE_LOCKED.</summary>
    internal static readonly Counter<long> Retries = Meter.CreateCounter<long>(
        "sqlitegate.retry.count",
        unit: "{retry}",
        description: "Operations retried after SQLite reported SQLITE_BUSY or SQLITE_LOCKED.");

    /// <summary>Completed operations, tagged with their outcome.</summary>
    internal static readonly Counter<long> Operations = Meter.CreateCounter<long>(
        "sqlitegate.operation.count",
        unit: "{operation}",
        description: "Gated operations that ran to completion, tagged read/write and success/failure.");
}
