namespace PepperX.SqliteGate;

/// <summary>
/// Entry point to the library: hands you the gate for a database file.
/// </summary>
/// <example>
/// <code>
/// var gate = SqliteGate.For("app.db", options =>
/// {
///     options.BusyTimeout = TimeSpan.FromSeconds(5);
///     options.MaxRetryAttempts = 5;
/// });
///
/// await gate.WriteAsync(async (connection, ct) =&gt;
/// {
///     await using var command = connection.CreateCommand();
///     command.CommandText = "INSERT INTO Todos (Title) VALUES ($title)";
///     command.Parameters.AddWithValue("$title", "Buy milk");
///     await command.ExecuteNonQueryAsync(ct);
/// });
///
/// var count = await gate.ReadAsync(async (connection, ct) =&gt;
/// {
///     await using var command = connection.CreateCommand();
///     command.CommandText = "SELECT COUNT(*) FROM Todos";
///     return (long)(await command.ExecuteScalarAsync(ct))!;
/// });
/// </code>
/// </example>
public static class SqliteGate
{
    /// <summary>
    /// Returns the process-wide gate for a database, creating it on first use.
    /// </summary>
    /// <param name="databasePathOrConnectionString">
    /// A file path (relative or absolute), or a full <c>Microsoft.Data.Sqlite</c> connection string.
    /// Anything containing <c>'='</c> is treated as a connection string.
    /// </param>
    /// <param name="configure">
    /// Applied only when this call creates the gate. Callers naming a database that is already
    /// gated get the existing instance, configuration and all.
    /// </param>
    /// <returns>The shared gate for that database.</returns>
    public static ISqliteGate For(string databasePathOrConnectionString, Action<SqliteGateOptions>? configure = null) =>
        SqliteGateRegistry.For(databasePathOrConnectionString, configure);
}
