using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace PepperX.SqliteGate.Internals;

/// <summary>
/// Exponential backoff with jitter for the contention the queue cannot remove.
/// </summary>
/// <remarks>
/// <para>
/// Serializing writes inside one process does not make <c>SQLITE_BUSY</c> impossible. Another
/// process can hold the file; a WAL checkpoint can collide with a reader; a long-lived read snapshot
/// can make a writer fail with <c>SQLITE_BUSY_SNAPSHOT</c>. Those are transient by definition, so
/// they are worth retrying — and only those. Every other <see cref="SqliteException"/> (constraint
/// violations, syntax errors, a missing table) is a genuine answer and is rethrown untouched.
/// </para>
/// <para>
/// When the attempts or the time budget run out, the <em>original</em> exception is propagated
/// rather than a wrapper, so callers that already handle <see cref="SqliteException"/> keep working.
/// </para>
/// </remarks>
internal sealed class RetryExecutor
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    private readonly SqliteGateOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly string _database;
    private readonly KeyValuePair<string, object?>[] _metricTags;
    private readonly bool _metricsEnabled;

    internal RetryExecutor(
        SqliteGateOptions options,
        string database,
        ILogger logger,
        KeyValuePair<string, object?>[] metricTags)
    {
        _options = options;
        _timeProvider = options.TimeProvider;
        _logger = logger;
        _database = database;
        _metricTags = metricTags;
        _metricsEnabled = options.EnableMetrics;
    }

    /// <summary>Runs <paramref name="operation"/>, retrying only transient SQLite contention.</summary>
    /// <typeparam name="T">Type produced by the operation.</typeparam>
    /// <param name="operation">Receives the zero-based attempt number.</param>
    /// <param name="cancellationToken">Observed between attempts and during the backoff delay.</param>
    /// <returns>Whatever the operation returned on its first successful attempt.</returns>
    internal async Task<T> ExecuteAsync<T>(Func<int, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        var startedAt = _timeProvider.GetTimestamp();
        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await operation(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (IsTransient(ex) && CanRetry(attempt, startedAt))
            {
                attempt++;
                var delay = ComputeDelay(attempt);

                if (_metricsEnabled)
                {
                    SqliteGateMetrics.Retries.Add(1, _metricTags);
                }

                Log.RetryingAfterContention(
                    _logger,
                    _database,
                    ex.SqliteErrorCode,
                    (long)delay.TotalMilliseconds,
                    attempt,
                    _options.MaxRetryAttempts);

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (SqliteException ex) when (IsTransient(ex))
            {
                // Budget spent. Log the shape of the failure, then let the original exception out
                // untouched so existing SqliteException handling still applies.
                Log.RetriesExhausted(
                    _logger,
                    _database,
                    attempt + 1,
                    (long)_timeProvider.GetElapsedTime(startedAt).TotalMilliseconds,
                    ex);
                throw;
            }
        }
    }

    private bool CanRetry(int attempt, long startedAt)
    {
        if (attempt >= _options.MaxRetryAttempts)
        {
            return false;
        }

        return _timeProvider.GetElapsedTime(startedAt) < _options.MaxTotalRetryDuration;
    }

    internal TimeSpan ComputeDelay(int attempt)
    {
        if (_options.InitialRetryDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        // Doubling from the initial delay, clamped, then spread by jitter so a set of writers that
        // were rejected together does not come back in lockstep.
        var exponent = Math.Min(attempt - 1, 30);
        var scaled = _options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var capped = Math.Min(scaled, _options.MaxRetryDelay.TotalMilliseconds);

        if (_options.RetryJitter > 0)
        {
            var spread = capped * _options.RetryJitter;
            capped += (Random.Shared.NextDouble() * 2 - 1) * spread;
        }

        return TimeSpan.FromMilliseconds(Math.Max(0, capped));
    }

    /// <summary>True for the two error codes that mean "someone else has the lock, try again".</summary>
    internal static bool IsTransient(SqliteException exception) =>
        exception.SqliteErrorCode is SqliteBusy or SqliteLocked;
}
