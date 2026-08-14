using Microsoft.Data.Sqlite;

namespace PepperX.SqliteGate.Internals;

/// <summary>
/// Turns whatever the caller passed — a bare path, a relative path, a full connection string — into
/// the single canonical key the registry uses for a database.
/// </summary>
/// <remarks>
/// This type is small and dull and it is the load-bearing piece of the whole library. Two callers
/// that name the same file differently (<c>"app.db"</c> from one working directory,
/// <c>"/srv/app/app.db"</c> from a helper, <c>"Data Source=./app.db"</c> from EF Core) must land on
/// the same key, or they get two gates, two write queues, no mutual exclusion, and
/// <c>SQLITE_BUSY</c> returns while every test still passes. Canonicalisation happens here and only
/// here.
/// </remarks>
internal sealed record DatabaseIdentity(string Key, string DisplayPath, string ConnectionString, bool IsInMemory)
{
    /// <summary>
    /// Paths compare case-insensitively everywhere except Linux, matching how the operating systems
    /// themselves resolve them.
    /// </summary>
    internal static StringComparer KeyComparer { get; } =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    internal static DatabaseIdentity Resolve(string databasePathOrConnectionString, SqliteGateOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePathOrConnectionString);

        // A '=' is the only reliable signal that we were handed a connection string rather than a
        // path: SqliteConnectionStringBuilder throws on a bare path, and a path containing '=' is
        // rare enough to be worth documenting instead of guessing at.
        var builder = databasePathOrConnectionString.Contains('=', StringComparison.Ordinal)
            ? new SqliteConnectionStringBuilder(databasePathOrConnectionString)
            : new SqliteConnectionStringBuilder { DataSource = databasePathOrConnectionString };

        var dataSource = builder.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            throw new ArgumentException(
                "The connection string does not name a data source.",
                nameof(databasePathOrConnectionString));
        }

        var inMemory = IsInMemoryDataSource(dataSource, builder.Mode);

        string key;
        string displayPath;

        if (inMemory)
        {
            // In-memory databases have no path to canonicalise. Two callers reach the same database
            // only when they name the same data source with the same cache mode, so those two things
            // — and nothing else from the connection string — make the key. Keeping tuning options
            // out of it means the same database resolves to the same gate however it was configured.
            displayPath = dataSource;
            key = $"memory:{dataSource}|cache={builder.Cache}";
        }
        else
        {
            displayPath = Path.GetFullPath(dataSource);
            builder.DataSource = displayPath;
            key = "file:" + displayPath;
        }

        // Take charge of the provider's own busy-retry loop. Microsoft.Data.Sqlite retries locked
        // statements in managed code for CommandTimeout seconds — 30 by default, and unaffected by
        // PRAGMA busy_timeout — so leaving this alone would hide contention from this library's
        // retry policy for half a minute. The setting is whole seconds, and zero means "wait
        // forever", so sub-second values round up to one rather than down to nothing.
        var commandTimeout = options.CommandTimeout ?? options.BusyTimeout;
        builder.DefaultTimeout = Math.Max(1, (int)Math.Ceiling(commandTimeout.TotalSeconds));

        options.ConfigureConnectionString?.Invoke(builder);

        // The data source is fixed after configuration: letting a callback repoint it would key the
        // gate on one database while it opens another.
        builder.DataSource = inMemory ? dataSource : displayPath;

        return new DatabaseIdentity(key, displayPath, builder.ConnectionString, inMemory);
    }

    private static bool IsInMemoryDataSource(string dataSource, SqliteOpenMode mode)
    {
        if (mode == SqliteOpenMode.Memory)
        {
            return true;
        }

        if (string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // URI filenames: file::memory:, file:name?mode=memory
        return dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            && (dataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase)
                || dataSource.Contains("mode=memory", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Short name used as a metric tag, so cardinality stays low and paths stay private.</summary>
    internal string MetricName => IsInMemory ? DisplayPath : Path.GetFileName(DisplayPath);
}
