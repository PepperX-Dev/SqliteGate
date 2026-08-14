using System.Runtime.CompilerServices;

namespace PepperX.SqliteGate.Conformance;

/// <summary>
/// Test-host setup that the concurrency suites depend on.
/// </summary>
public static class TestHostConfiguration
{
    /// <summary>
    /// Raises the thread pool's floor before any suite runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SQLite has no asynchronous I/O, so <c>ExecuteNonQueryAsync</c> and friends do their work on
    /// the calling thread and hand back an already-completed task. Two hundred concurrent writers
    /// therefore genuinely occupy two hundred pool threads for a moment, and the pool only injects
    /// new threads at roughly one or two per second once its minimum is exhausted. Left alone, the
    /// stress suites spend their time queued behind thread injection, which turns any assertion
    /// about promptness into an assertion about the scheduler.
    /// </para>
    /// <para>
    /// This raises the floor rather than papering over a latency bug: production code that awaits
    /// these calls behaves the same way, which is worth knowing and is documented in the README.
    /// </para>
    /// </remarks>
    // CA2255 warns that module initializers are for application code, because a library imposing
    // process-wide settings on its consumers is rude. The only consumers here are this repository's
    // test projects, and configuring the test host is precisely the intent.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    public static void Initialize()
    {
        ThreadPool.GetMinThreads(out var workerThreads, out var completionPortThreads);
        ThreadPool.SetMinThreads(Math.Max(workerThreads, 256), Math.Max(completionPortThreads, 256));
    }
}
