[![Part of PepperX Ecosystem](https://img.shields.io/badge/Part_of-PepperX_Ecosystem-512BD4?style=for-the-badge&logo=github&logoColor=white)](https://github.com/PepperX-Dev)

![PepperX.SqliteGate.Dapper Logo](https://raw.githubusercontent.com/PepperX-Dev/SqliteGate/main/icon.png)

# PepperX.SqliteGate.Dapper

[![NuGet Version](https://img.shields.io/nuget/v/PepperX.SqliteGate.Dapper.svg?style=flat-square&label=PepperX.SqliteGate.Dapper)](https://www.nuget.org/packages/PepperX.SqliteGate.Dapper/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg?style=flat-square)](https://github.com/PepperX-Dev/SqliteGate/blob/main/LICENSE)

> **Using Dapper with SQLite and hitting `database is locked`?**
> Same Dapper methods, one letter different at the call site, and the errors stop.

---

## 😖 The problem

Dapper made your data access two lines long. It did not make SQLite accept two writers.

```csharp
// A repository method, called from 200 concurrent requests
await using var connection = new SqliteConnection("Data Source=app.db");

await connection.ExecuteAsync(
    "INSERT INTO Todos (Title) VALUES (@Title)",
    new { Title = "Buy milk" });
```

```
Microsoft.Data.Sqlite.SqliteException (0x80004005): SQLite Error 5: 'database is locked'.
```

Dapper is a mapper, not a scheduler. It faithfully hands your command to a connection — and that
connection has no idea another one is mid-write against the same file. SQLite allows **many readers,
one writer**, and nothing in this stack is enforcing that for you.

## ✅ The fix

```csharp
var gate = SqliteGate.For("app.db");          // ← call it on the gate, not the connection

await gate.ExecuteAsync(
    "INSERT INTO Todos (Title) VALUES (@Title)",
    new { Title = "Buy milk" });

var todos = await gate.QueryAsync<Todo>("SELECT * FROM Todos");
```

The method names, the parameter objects, the mapping — all unchanged. The only difference is what
you call them **on**. `Execute*` now goes through a single-writer queue; `Query*` runs on a pool of
reader connections that never waits for a writer.

---

## 🤔 Why this exists

You could do this yourself. Three reasons it goes wrong:

| The DIY version | What breaks |
| :--- | :--- |
| **A `SemaphoreSlim` in your repository** | Works until a second repository, a background job, or an EF Core `DbContext` touches the same file. Each one gets its own perfectly orderly queue, and they collide with each other. |
| **`PRAGMA busy_timeout` in your connection string** | It's per connection, so it evaporates on every new one Dapper opens — and `Microsoft.Data.Sqlite` runs its own hidden 30-second retry loop *in front of it* that ignores the pragma entirely. |
| **Retrying `SqliteException` around `ExecuteAsync`** | A multi-statement write that failed halfway re-applies the rows it already committed. |

This package is a **thin adapter** — perhaps 300 lines of extension methods. Every hard part (the
queue, WAL setup, pragmas re-applied per connection, retry with jitter, metrics) lives in
[`PepperX.SqliteGate`](https://github.com/PepperX-Dev/SqliteGate/tree/main/src/PepperX.SqliteGate), which ships with it. That matters because the gate is
keyed on the **database file**: your Dapper calls, someone else's raw ADO.NET, and an EF Core
`DbContext` all queue behind *the same* write slot, automatically.

Writes are wrapped in a transaction that is passed to Dapper explicitly, so a retry after contention
rolls the whole attempt back instead of applying half of it twice.

---

## 🎯 Is this the package you need?

| How you talk to SQLite | Install |
| :--- | :--- |
| **Dapper** | **This package** ✅ |
| Raw ADO.NET (`Microsoft.Data.Sqlite`) | [`PepperX.SqliteGate`](https://github.com/PepperX-Dev/SqliteGate/tree/main/src/PepperX.SqliteGate) |
| EF Core | [`PepperX.SqliteGate.EFCore`](https://github.com/PepperX-Dev/SqliteGate/tree/main/src/PepperX.SqliteGate.EFCore) |
| A mix of them | Install each — one gate per file, shared automatically |

**Skip it if:** you only ever write from one thread, or you need locking coordinated across
*separate processes* (SQLite's own file locking still governs that).

---

## ⚡ At a glance

- 🎯 **The methods you already write** — `QueryAsync`, `ExecuteAsync`, and the rest, on `ISqliteGate`.
- 🚦 **Routing you can see** — `Query*` reads, `Execute*` writes. Decided by the name, never by parsing your SQL.
- 📝 **Writes are transactional** — the transaction is passed to Dapper, so retries are safe.
- 📦 **Batches** — many statements, one transaction, one turn in the queue.
- 🔗 **One gate per file** — shared with the ADO.NET and EF Core packages in the same process.
- 🪶 **Thin by design** — no new concurrency logic here; it all lives in the core engine.

## Install

```bash
dotnet add package PepperX.SqliteGate.Dapper
```

## The routing rule

**`Query*` reads. `Execute*` writes.** That is the whole rule, and it is decided by the method you
called — never by looking at your SQL.

| Method | Path | Notes |
| :--- | :--- | :--- |
| `QueryAsync<T>` | 📖 Reader pool | Buffered |
| `QueryFirstAsync<T>` / `QueryFirstOrDefaultAsync<T>` | 📖 Reader pool | |
| `QuerySingleAsync<T>` / `QuerySingleOrDefaultAsync<T>` | 📖 Reader pool | Use these for scalar **reads** |
| `QueryBatchAsync<T>` | 📖 Reader pool | Several reads on one connection, one snapshot |
| `ExecuteAsync` | 📝 Write queue | Returns rows affected |
| `ExecuteScalarAsync<T>` | 📝 Write queue | For `INSERT … RETURNING Id` |
| `ExecuteReturningAsync<T>` | 📝 Write queue | For `INSERT`/`UPDATE … RETURNING *` |
| `ExecuteBatchAsync` | 📝 Write queue | Several writes, one transaction, one turn in the queue |

Dapper's own vocabulary already draws this line, which is why following it keeps the rule visible at
the call site rather than hidden in an inference the library makes on your behalf.

### The one that surprises people

`ExecuteScalarAsync<T>` is an **`Execute`**, so it is a **write**. That is what makes
`INSERT … RETURNING Id` correct:

```csharp
var id = await gate.ExecuteScalarAsync<long>(
    "INSERT INTO Todos (Title) VALUES (@Title) RETURNING Id;",
    new { Title = "Buy milk" });
```

For a scalar that only reads, use `QuerySingleAsync<T>`, which is on the read path:

```csharp
var count = await gate.QuerySingleAsync<long>("SELECT COUNT(*) FROM Todos;");
```

Routing `ExecuteScalarAsync` by its name rather than by its usual `SELECT COUNT(*)` usage is a
deliberate trade: consistency you can predict beats a special case you have to remember.

## Reading

```csharp
var todos = await gate.QueryAsync<Todo>(
    "SELECT Id, Title FROM Todos WHERE Done = @Done ORDER BY Id DESC",
    new { Done = false });

var one = await gate.QuerySingleOrDefaultAsync<Todo>(
    "SELECT * FROM Todos WHERE Id = @Id", new { Id = 42 });
```

Results are **buffered**, and have to be: the reader connection returns to the pool as soon as the
call completes, so a lazily-enumerated result would be reading from a connection somebody else
already has.

## Writing

```csharp
await gate.ExecuteAsync(
    "UPDATE Todos SET Done = 1 WHERE Id = @Id",
    new { Id = 42 });

// Dapper's list expansion still works — one turn in the queue, one transaction
await gate.ExecuteAsync(
    "INSERT INTO Todos (Title) VALUES (@Title)",
    new[] { new { Title = "one" }, new { Title = "two" } });
```

Every write runs inside the gate's transaction, and the transaction is passed to Dapper explicitly,
so a retry after contention rolls the whole attempt back rather than applying half of it twice.

## Batches

When several statements must commit together — and should only occupy the write slot once:

```csharp
await gate.ExecuteBatchAsync(async (connection, transaction, ct) =>
{
    var orderId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
        "INSERT INTO Orders (Reference) VALUES (@Reference) RETURNING Id;",
        new { order.Reference }, transaction, cancellationToken: ct));

    await connection.ExecuteAsync(new CommandDefinition(
        "INSERT INTO OrderLines (OrderId, Sku) VALUES (@OrderId, @Sku);",
        lines.Select(l => new { OrderId = orderId, l.Sku }), transaction, cancellationToken: ct));
});
```

Pass the `transaction` to each `CommandDefinition`. Throw, and nothing commits.

There is a read counterpart, `QueryBatchAsync`, for several queries that should see one consistent
snapshot.

## Read-modify-write

If the new value depends on the old one, both halves have to be inside the same gated write —
otherwise two callers can read the same value and each write back the same increment:

```csharp
await gate.ExecuteBatchAsync(async (connection, transaction, ct) =>
{
    var current = await connection.QuerySingleAsync<long>(new CommandDefinition(
        "SELECT Value FROM Counters WHERE Name = 'hits';", transaction: transaction, cancellationToken: ct));

    await connection.ExecuteAsync(new CommandDefinition(
        "UPDATE Counters SET Value = @Value WHERE Name = 'hits';",
        new { Value = current + 1 }, transaction, cancellationToken: ct));
});
```

The concurrency suite runs exactly this shape 200 times over and asserts the counter lands on 200.

## Sharing the gate

The gate is keyed on the database file, so Dapper code and everything else in the process queue
together automatically:

```csharp
services.AddSqliteGate("app.db");

// In a repository
public sealed class TodoRepository(ISqliteGate gate)
{
    public Task<IEnumerable<Todo>> GetAllAsync(CancellationToken ct = default) =>
        gate.QueryAsync<Todo>("SELECT * FROM Todos", cancellationToken: ct);

    public Task<int> AddAsync(string title, CancellationToken ct = default) =>
        gate.ExecuteAsync("INSERT INTO Todos (Title) VALUES (@Title)", new { Title = title }, cancellationToken: ct);
}
```

## Going deeper

Configuration (busy timeout, retry policy, reader pool size), the metrics, WAL setup and the
one-gate-per-file rule are all documented in the core package:
[`PepperX.SqliteGate`](https://github.com/PepperX-Dev/SqliteGate/tree/main/src/PepperX.SqliteGate).

## 🤝 Contributing & License

This project is part of the [PepperX Ecosystem](https://github.com/PepperX-Dev).

Licensed under the **MIT License** — see [LICENSE](https://github.com/PepperX-Dev/SqliteGate/blob/main/LICENSE).