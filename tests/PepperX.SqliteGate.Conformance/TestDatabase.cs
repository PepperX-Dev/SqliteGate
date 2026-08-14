using System.Runtime.CompilerServices;

namespace PepperX.SqliteGate.Conformance;

/// <summary>
/// A throwaway SQLite file in its own temporary directory, plus the cleanup that concurrency tests
/// need: the gate is removed from the process-wide registry on disposal, so the next test starts
/// from nothing rather than inheriting a warm queue.
/// </summary>
/// <remarks>
/// Tests use real files on purpose. File locking <em>is</em> the subject — an <c>:memory:</c>
/// database has no locks to contend for, so a suite built on one would pass whether or not the
/// library worked.
/// </remarks>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _directory;

    private TestDatabase(string directory, string path)
    {
        _directory = directory;
        Path = path;
    }

    /// <summary>Full path of the database file.</summary>
    public string Path { get; }

    /// <summary>Creates a uniquely named database file under the system temp directory.</summary>
    /// <param name="name">Defaults to the calling member, so failures name the test that leaked.</param>
    /// <returns>The database handle, which deletes the file when disposed.</returns>
    public static TestDatabase Create([CallerMemberName] string? name = null)
    {
        var token = Guid.NewGuid().ToString("N");
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "sqlitegate-tests",
            $"{name ?? "db"}-{token}");

        Directory.CreateDirectory(directory);

        // The file name is unique, not just the directory: it becomes the "db.name" metric tag, and
        // tests that read metrics need to tell their own measurements from those of tests running
        // beside them.
        return new TestDatabase(directory, System.IO.Path.Combine(directory, $"{name ?? "db"}-{token}.db"));
    }

    /// <summary>Releases the gate and deletes the file, the WAL, and the shared-memory index.</summary>
    public async ValueTask DisposeAsync()
    {
        await SqliteGateRegistry.RemoveAsync(Path).ConfigureAwait(false);

        // Best effort: a leftover temp directory is untidy, never a test failure.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }
}
