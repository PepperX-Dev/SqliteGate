using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace PepperX.SqliteGate.Conformance;

/// <summary>
/// Runs a batch of operations that all start at the same instant, and reports what broke.
/// </summary>
/// <remarks>
/// Tasks are held at a starting gate and released together. Without that, spinning up 200 tasks in a
/// loop lets the first ones finish before the last ones exist, and the test quietly stops being
/// about concurrency at all.
/// </remarks>
public static class ConcurrencyHarness
{
    /// <summary>The load every package is measured at.</summary>
    public const int DefaultConcurrency = 200;

    /// <summary>
    /// How long a stress run may take before it is treated as deadlocked. Generous enough for a
    /// loaded CI runner, short enough that a livelock fails the build instead of hanging it.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Starts <paramref name="degreeOfParallelism"/> operations together and collects their failures.</summary>
    /// <param name="degreeOfParallelism">Number of concurrent operations.</param>
    /// <param name="operation">Receives the zero-based ordinal of the operation.</param>
    /// <param name="timeout">Deadlock guard. Defaults to <see cref="DefaultTimeout"/>.</param>
    /// <returns>Every exception thrown, in no particular order. Empty means a clean run.</returns>
    public static async Task<IReadOnlyList<Exception>> RunConcurrentlyAsync(
        int degreeOfParallelism,
        Func<int, CancellationToken, Task> operation,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var limit = timeout ?? DefaultTimeout;
        using var cancellation = new CancellationTokenSource(limit);

        var startingGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new ConcurrentBag<Exception>();
        var tasks = new Task[degreeOfParallelism];

        for (var index = 0; index < degreeOfParallelism; index++)
        {
            var ordinal = index;
            tasks[index] = Task.Run(async () =>
            {
                await startingGate.Task.ConfigureAwait(false);

                try
                {
                    await operation(ordinal, cancellation.Token).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            });
        }

        startingGate.SetResult();

        // The grace period exists so that a genuine hang surfaces as the operations' own cancellation
        // rather than as an opaque timeout here.
        await Task.WhenAll(tasks).WaitAsync(limit + TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        return [.. failures];
    }

    /// <summary>Picks the "database is locked" failures out of a set of exceptions.</summary>
    /// <param name="exceptions">Exceptions collected from a run.</param>
    /// <returns>Only those that are SQLITE_BUSY (5) or SQLITE_LOCKED (6).</returns>
    public static IReadOnlyList<SqliteException> ContentionErrors(IEnumerable<Exception> exceptions) =>
        [.. exceptions.SelectMany(Flatten).OfType<SqliteException>().Where(IsContention)];

    /// <summary>True if the exception is SQLite reporting that someone else holds the lock.</summary>
    /// <param name="exception">The exception to classify.</param>
    /// <returns><see langword="true"/> for SQLITE_BUSY and SQLITE_LOCKED.</returns>
    public static bool IsContention(SqliteException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.SqliteErrorCode is 5 or 6;
    }

    /// <summary>Renders failures compactly enough to read in a CI log.</summary>
    /// <param name="exceptions">Exceptions collected from a run.</param>
    /// <returns>A newline-separated summary, or a note that there were none.</returns>
    public static string Describe(IEnumerable<Exception> exceptions)
    {
        ArgumentNullException.ThrowIfNull(exceptions);

        var listed = exceptions
            .SelectMany(Flatten)
            .GroupBy(exception => $"{exception.GetType().Name}: {exception.Message}")
            .Select(group => $"  ({group.Count()}x) {group.Key}")
            .ToArray();

        return listed.Length == 0 ? "  (none)" : string.Join(Environment.NewLine, listed);
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
            {
                yield return inner;
            }

            yield break;
        }

        yield return exception;
    }
}
