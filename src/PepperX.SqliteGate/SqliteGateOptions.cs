using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace PepperX.SqliteGate;

/// <summary>
/// Configuration for a single <see cref="ISqliteGate"/>.
/// </summary>
/// <remarks>
/// <para>
/// Options are applied when a gate is first created for a database file and are frozen from that
/// point on. Because gates are shared process-wide per database file (see
/// <see cref="SqliteGateRegistry"/>), a second caller asking for the same file receives the gate
/// that already exists and its own configuration callback is ignored — there is exactly one set of
/// concurrency settings per database, which is the only way the guarantee can hold.
/// </para>
/// </remarks>
public sealed class SqliteGateOptions
{
    private static readonly int DefaultReaderCount = Math.Clamp(Environment.ProcessorCount, 4, 16);

    /// <summary>
    /// How long SQLite waits for a lock held by another connection (or another process) before
    /// giving up with <c>SQLITE_BUSY</c>. Applied as <c>PRAGMA busy_timeout</c> to every connection
    /// the gate opens. Default: 5 seconds.
    /// </summary>
    public TimeSpan BusyTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Value written to the connection string's <c>Default Timeout</c>, which is what bounds the
    /// retry loop <c>Microsoft.Data.Sqlite</c> runs <em>itself</em> whenever SQLite reports a locked
    /// database. When left <see langword="null"/> (the default) it follows <see cref="BusyTimeout"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not a redundant third timeout, it is the one that would otherwise win. The provider
    /// retries busy statements in managed code for <c>CommandTimeout</c> seconds — 30 by default —
    /// before it ever surfaces a <see cref="SqliteException"/>, and it does so regardless of
    /// <c>PRAGMA busy_timeout</c>. Leaving it at the default would mean a contended write blocks for
    /// half a minute inside the provider while this library's own busy timeout and retry policy sit
    /// unused behind it. Aligning the two puts the configured behaviour back in charge.
    /// </para>
    /// <para>
    /// Two sharp edges are worth knowing: the underlying setting is a whole number of
    /// <em>seconds</em>, so sub-second values round up to one second, and a value of zero means
    /// <em>wait forever</em> rather than "do not wait" — which is why the gate never writes zero.
    /// The timeout only ever applies while the database is locked; it does not interrupt a
    /// long-running query.
    /// </para>
    /// </remarks>
    public TimeSpan? CommandTimeout { get; set; }

    /// <summary>
    /// Durability level applied as <c>PRAGMA synchronous</c>. Default:
    /// <see cref="SqliteSynchronousMode.Normal"/>, which is the recommended setting for
    /// write-ahead logging — durable across application crashes, and only at risk of losing the
    /// most recent commits if the machine itself loses power.
    /// </summary>
    public SqliteSynchronousMode Synchronous { get; set; } = SqliteSynchronousMode.Normal;

    /// <summary>
    /// Whether to apply <c>PRAGMA foreign_keys=ON</c> to every connection. SQLite defaults this to
    /// <c>OFF</c> for backwards compatibility. Default: <see langword="true"/>.
    /// </summary>
    public bool EnableForeignKeys { get; set; } = true;

    /// <summary>
    /// Whether the gate refuses to start when write-ahead logging cannot be enabled. Default:
    /// <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Free concurrent reads are a direct consequence of WAL: in the default rollback journal mode a
    /// writer blocks every reader, so the library's central promise silently stops holding. Rather
    /// than degrade quietly, the gate fails loudly. Set this to <see langword="false"/> only if you
    /// knowingly run on a filesystem that cannot support WAL (some network shares), and accept that
    /// reads will then queue behind writes.
    /// </remarks>
    public bool RequireWriteAheadLog { get; set; } = true;

    /// <summary>
    /// Maximum number of pooled reader connections. Reads never queue behind a writer, but they do
    /// share this bounded pool so that a burst of traffic cannot open an unbounded number of file
    /// handles. Default: <see cref="Environment.ProcessorCount"/> clamped to the range 4–16.
    /// </summary>
    public int MaxReaderConnections { get; set; } = DefaultReaderCount;

    /// <summary>
    /// Maximum number of <em>retries</em> after a failed attempt when SQLite reports
    /// <c>SQLITE_BUSY</c> or <c>SQLITE_LOCKED</c>. A value of <c>0</c> disables retrying. Default: 5.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 5;

    /// <summary>
    /// Backoff delay before the first retry. Each further retry doubles it, up to
    /// <see cref="MaxRetryDelay"/>. Default: 50 ms.
    /// </summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Upper bound for a single backoff delay. Default: 1 second.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Upper bound on the total time spent retrying one operation, measured from the first attempt.
    /// Once exceeded, the original <see cref="SqliteException"/> is rethrown even if
    /// <see cref="MaxRetryAttempts"/> has not been reached. Default: 30 seconds.
    /// </summary>
    public TimeSpan MaxTotalRetryDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Proportion of random jitter applied to each backoff delay, in the range 0–1. At the default
    /// of <c>0.25</c> a computed 100 ms delay is drawn uniformly from 75–125 ms, which stops a set
    /// of writers that were rejected together from retrying in lockstep.
    /// </summary>
    public double RetryJitter { get; set; } = 0.25;

    /// <summary>
    /// Maximum time a write may spend waiting for its turn in the write queue before the gate gives
    /// up with a <see cref="SqliteGateTimeoutException"/>. <see langword="null"/> (the default)
    /// means wait indefinitely — the operation still observes its <see cref="CancellationToken"/>.
    /// </summary>
    public TimeSpan? WriteQueueTimeout { get; set; }

    /// <summary>
    /// Whether the gate reports to the <c>PepperX.SqliteGate</c> <see cref="System.Diagnostics.Metrics.Meter"/>.
    /// Default: <see langword="true"/>.
    /// </summary>
    public bool EnableMetrics { get; set; } = true;

    /// <summary>
    /// Logger factory used for queue waits, retries and pragma setup. When left
    /// <see langword="null"/> the gate logs nothing.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>
    /// Time source used for backoff delays and duration measurement. Exposed so tests can drive
    /// retry timing deterministically. Default: <see cref="System.TimeProvider.System"/>.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Hook to adjust the connection string the gate builds — cache mode, password, read-only flags,
    /// and anything else <see cref="SqliteConnectionStringBuilder"/> exposes. The data source is set
    /// before this runs; changing it is ignored, because the gate is already keyed on the original
    /// file.
    /// </summary>
    public Action<SqliteConnectionStringBuilder>? ConfigureConnectionString { get; set; }

    /// <summary>
    /// Extra <c>PRAGMA</c> statements applied to every connection the gate opens, after the built-in
    /// ones. Supply them exactly as SQLite expects, for example <c>"PRAGMA cache_size=-64000;"</c>.
    /// </summary>
    public IList<string> AdditionalPragmas { get; } = [];

    /// <summary>
    /// Validates the option values and throws <see cref="ArgumentOutOfRangeException"/> on the first
    /// one that cannot work.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its supported range.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(BusyTimeout, TimeSpan.Zero, nameof(BusyTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxReaderConnections, 1, nameof(MaxReaderConnections));
        ArgumentOutOfRangeException.ThrowIfNegative(MaxRetryAttempts, nameof(MaxRetryAttempts));
        ArgumentOutOfRangeException.ThrowIfLessThan(InitialRetryDelay, TimeSpan.Zero, nameof(InitialRetryDelay));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRetryDelay, InitialRetryDelay, nameof(MaxRetryDelay));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTotalRetryDuration, TimeSpan.Zero, nameof(MaxTotalRetryDuration));
        ArgumentOutOfRangeException.ThrowIfNegative(RetryJitter, nameof(RetryJitter));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RetryJitter, 1.0, nameof(RetryJitter));

        if (CommandTimeout is { } commandTimeout && commandTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CommandTimeout),
                commandTimeout,
                "The command timeout cannot be negative.");
        }

        if (WriteQueueTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(WriteQueueTimeout),
                timeout,
                "The write queue timeout must be positive, or null to wait indefinitely.");
        }
    }

    internal SqliteGateOptions Clone()
    {
        var clone = new SqliteGateOptions
        {
            BusyTimeout = BusyTimeout,
            CommandTimeout = CommandTimeout,
            Synchronous = Synchronous,
            EnableForeignKeys = EnableForeignKeys,
            RequireWriteAheadLog = RequireWriteAheadLog,
            MaxReaderConnections = MaxReaderConnections,
            MaxRetryAttempts = MaxRetryAttempts,
            InitialRetryDelay = InitialRetryDelay,
            MaxRetryDelay = MaxRetryDelay,
            MaxTotalRetryDuration = MaxTotalRetryDuration,
            RetryJitter = RetryJitter,
            WriteQueueTimeout = WriteQueueTimeout,
            EnableMetrics = EnableMetrics,
            LoggerFactory = LoggerFactory,
            TimeProvider = TimeProvider,
            ConfigureConnectionString = ConfigureConnectionString,
        };

        foreach (var pragma in AdditionalPragmas)
        {
            clone.AdditionalPragmas.Add(pragma);
        }

        return clone;
    }
}

/// <summary>
/// Values accepted by <c>PRAGMA synchronous</c>, in increasing order of durability and cost.
/// </summary>
public enum SqliteSynchronousMode
{
    /// <summary>No fsync at all. Fastest, and a machine crash can corrupt the database.</summary>
    Off = 0,

    /// <summary>
    /// Fsync at checkpoints only. The recommended pairing with WAL: safe against application
    /// crashes, and a power failure can only cost the most recent transactions.
    /// </summary>
    Normal = 1,

    /// <summary>Fsync on every commit. Safe against power loss, and materially slower.</summary>
    Full = 2,

    /// <summary>Like <see cref="Full"/>, and also syncs the directory entry on commit.</summary>
    Extra = 3,
}
