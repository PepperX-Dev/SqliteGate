namespace PepperX.SqliteGate.Conformance;

/// <summary>
/// The schema every package's concurrency suite runs against. Shared so that "the same logical
/// scenario" really is the same scenario, down to the column types.
/// </summary>
public static class TestSchema
{
    /// <summary>
    /// A table to append rows to, and a single-row counter to fight over. The counter is what turns
    /// a read-modify-write into a lost update when writes are not serialized.
    /// </summary>
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS Todos (
            Id      INTEGER PRIMARY KEY AUTOINCREMENT,
            Title   TEXT    NOT NULL,
            Ordinal INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Counters (
            Name  TEXT    PRIMARY KEY,
            Value INTEGER NOT NULL
        );

        INSERT OR IGNORE INTO Counters (Name, Value) VALUES ('hits', 0);
        """;
}
