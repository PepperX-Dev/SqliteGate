[![Part of PepperX Ecosystem](https://img.shields.io/badge/Part_of-PepperX_Ecosystem-512BD4?style=for-the-badge&logo=github&logoColor=white)](https://github.com/PepperX-Dev)

![PepperX.SqliteGate Logo](https://raw.githubusercontent.com/PepperX-Dev/SqliteGate/main/icon.png)

# PepperX.SqliteGate

[![NuGet Version](https://img.shields.io/nuget/v/PepperX.SqliteGate.svg?style=flat-square&label=PepperX.SqliteGate)](https://www.nuget.org/packages/PepperX.SqliteGate/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg?style=flat-square)](https://github.com/PepperX-Dev/SqliteGate/blob/main/LICENSE)

> **Stop `SQLite Error 5: 'database is locked'` in a multi-threaded .NET app.**
> Writes queue, reads never block, and you configure nothing.

---

## 😖 The problem

Your app works. You ship it. Then two users show up at the same time.

```csharp
// Any web app under load: every request opens its own connection
await Parallel.ForEachAsync(Enumerable.Range(0, 200), async (i, ct) =>
{
    await using var connection = new SqliteConnection("Data Source=app.db");
    await connection.OpenAsync(ct);

    await using var command = connection.CreateCommand();
    command.CommandText = "INSERT INTO Todos (Title) VALUES ($title)";
    command.Parameters.AddWithValue("$title", $"Todo {i}");
    await command.ExecuteNonQueryAsync(ct);
});
```

```
Microsoft.Data.Sqlite.SqliteException (0x80004005): SQLite Error 5: 'database is locked'.
```

Nothing is wrong with your code. SQLite allows **many readers but only one writer**, and ADO.NET
opens connections that know nothing about each other. Two requests, one file, one loser.

## ✅ The fix

```csharp
var gate = SqliteGate.For("app.db");          // ← the only new line

await Parallel.ForEachAsync(Enumerable.Range(0, 200), async (i, ct) =>
{
    await gate.WriteAsync(async (connection, token) =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Todos (Title) VALUES ($title)";
        command.Parameters.AddWithValue("$title", $"Todo {i}");
        await command.ExecuteNonQueryAsync(token);
    }, ct);
});

// 200 rows. Zero exceptions. No pragmas, no retry loop, no lock statement.
```

Both snippets are real tests that run on every build —
[`BaselineWithoutGateTests`](https://github.com/PepperX-Dev/SqliteGate/blob/main/tests/PepperX.SqliteGate.Tests/BaselineWithoutGateTests.cs)
asserts the first one **fails** and the second one **doesn't**.

---

## 🤔 Why this exists

Everyone tries the same three fixes first. Here is why each one leaks.

| The usual fix | Why it isn't enough |
| :--- | :--- |
| **`PRAGMA busy_timeout=5000`** | Makes writers wait instead of failing — but it's **per connection**, so it silently vanishes on every new connection your pool opens. And without WAL, a writer still blocks every *reader*. |
| **`lock (_sync) { … }` around your data access** | Holds until one code path forgets, a background job is added, or someone calls Dapper directly. It also can't help EF Core, which opens its own connections. |
| **A retry loop on `SqliteException`** | Turns contention into latency, and a retried half-finished transaction re-applies the rows it already wrote. |

There's also a trap nobody finds until they measure: **`Microsoft.Data.Sqlite` runs its own hidden
busy-retry loop**, bounded by `CommandTimeout` — **30 seconds by default**, unaffected by
`PRAGMA busy_timeout`. Your carefully tuned settings sit behind it doing nothing. (And
`Default Timeout=0` means *wait forever*, not "don't wait" — that one hangs.)

SqliteGate handles all of it in one place: one write queue per database file, WAL so reads never
block, the right pragmas re-applied to every connection, and the provider's hidden timeout brought
under your control.

---

## 🎯 Is this the package you need?

| How you talk to SQLite | Install |
| :--- | :--- |
| **Raw ADO.NET** (`Microsoft.Data.Sqlite`) | **This package** ✅ |
| **Dapper** | [`PepperX.SqliteGate.Dapper`](https://github.com/PepperX-Dev/SqliteGate/tree/main/src/PepperX.SqliteGate.Dapper) |
| **EF Core** | [`PepperX.SqliteGate.EFCore`](https://github.com/PepperX-Dev/SqliteGate/tree/main/src/PepperX.SqliteGate.EFCore) |
| **A mix of them** | Install each — they share one gate per file automatically |

There is no separate `.Ado` package: this one *is* the ADO.NET experience, and it is the engine the
other two are built on.

**Skip it if:** your app only ever writes from one thread, or you need to coordinate writes between
*separate processes* — see [Scope](#scope).

---

## ⚡ At a glance

- 🚪 **One gate per database file, process-wide** — every caller shares one write queue.
- 📝 **Single-writer queue** — one write in flight per file, with observable depth.
- 📖 **Free concurrent reads** — a bounded pool of reader connections that never waits for a writer.
- ⚙️ **Automatic pragmas** — WAL, `synchronous`, `busy_timeout`, `foreign_keys`, on every connection.
- 🔁 **Retry with backoff and jitter** — for the residual contention a queue cannot remove.
- 📊 **Metrics and logging** — queue depth, wait time, retries, through `System.Diagnostics.Metrics`.
- 🧵 **Async-first** — `CancellationToken` honoured while queued and while backing off. No sync-over-async.

## Install

```bash
dotnet add package PepperX.SqliteGate
```

## Getting a gate

```csharp
var gate = SqliteGate.For("app.db", options =>
{
    options.BusyTimeout = TimeSpan.FromSeconds(5);
    options.MaxRetryAttempts = 5;
});
```

Or from dependency injection:

```csharp
services.AddSqliteGate("app.db", options => options.MaxRetryAttempts = 5);

// and for an app with more than one database:
services.AddKeyedSqliteGate("orders", "orders.db");
services.AddKeyedSqliteGate("audit",  "audit.db");
```

Both routes hand back **the same instance** for the same file. That is deliberate, and it is the
detail the whole library rests on — see [One gate per file](#one-gate-per-file-the-load-bearing-detail).

## Writing

```csharp
await gate.WriteAsync(async (connection, ct) =>
{
    await using var command = connection.CreateCommand();
    command.CommandText = "INSERT INTO Todos (Title) VALUES ($title)";
    command.Parameters.AddWithValue("$title", "Buy milk");
    await command.ExecuteNonQueryAsync(ct);
});
```

The delegate runs inside an implicit transaction, and commands created from the supplied connection
join it automatically. Writes can return values:

```csharp
var id = await gate.WriteAsync(async (connection, ct) =>
{
    await using var command = connection.CreateCommand();
    command.CommandText = "INSERT INTO Todos (Title) VALUES ($title) RETURNING Id";
    command.Parameters.AddWithValue("$title", "Buy milk");
    return (long)(await command.ExecuteScalarAsync(ct))!;
});
```

| Method | Transaction | Use it for |
| :--- | :--- | :--- |
| `WriteAsync` | Implicit, committed for you | Almost everything |
| `WriteTransactionAsync` | Handed to you explicitly | Libraries that need the transaction passed in — Dapper, for one |
| `WriteWithoutTransactionAsync` | None | `VACUUM`, `PRAGMA journal_mode`, and the few statements SQLite refuses to run in a transaction |

### Why writes are transactional by default

Because retrying is only safe if a failed attempt leaves nothing behind. A write that inserted three
rows and then met `SQLITE_BUSY` on the fourth would, on a naive retry, insert those three rows a
second time. Wrapping the delegate means the attempt is rolled back in full before the next one
starts.

`WriteWithoutTransactionAsync` is the exception, and it says so: with nothing to roll back, a retry
re-runs your delegate over whatever the failed attempt already committed. Keep those idempotent, or
set `MaxRetryAttempts = 0`.

## Reading

```csharp
var count = await gate.ReadAsync(async (connection, ct) =>
{
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM Todos";
    return (long)(await command.ExecuteScalarAsync(ct))!;
});
```

Reads borrow a connection from a bounded pool and **never** wait on the write queue. Under WAL a
reader works from a snapshot taken when its transaction starts, so it neither waits for the writer
nor makes the writer wait.

## Read and write intent is always explicit

The gate will not inspect your SQL to work out which path an operation belongs on. String-matching
for `SELECT` looks reasonable until you meet the statements where it is wrong — `INSERT … RETURNING`
starts with a write and produces rows; a `SELECT` over a virtual table can write; a CTE can hide
either inside it. A misclassified write is a write that skipped the queue, which brings back exactly
the errors the library exists to prevent, in the hardest possible way to debug.

So you say which you meant. `ReadAsync` or `WriteAsync`. That is the entire rule.

## One gate per file: the load-bearing detail

Gates are keyed on the **canonical full path** of the database, in a process-wide registry.

```csharp
SqliteGate.For("app.db")                        // ⎫
SqliteGate.For("./nested/../app.db")            // ⎬ all the same gate
SqliteGate.For("/srv/app/app.db")               // ⎪
SqliteGate.For("Data Source=app.db")            // ⎭
```

If this were keyed on the string you passed, or if each package built its own, you would get several
perfectly well-behaved write queues all feeding one file that accepts a single writer. Nothing would
look broken: each queue's metrics would be healthy and each package's tests would pass. You would
just get `SQLITE_BUSY` in production. [`RegistryTests`](https://github.com/PepperX-Dev/SqliteGate/blob/main/tests/PepperX.SqliteGate.Tests/RegistryTests.cs)
guards every spelling above, and that dependency injection and the static entry point return the
identical instance.

A value containing `=` is treated as a connection string; anything else is treated as a path.

## The pragmas, and why they are re-applied

| Pragma | Scope | Default here |
| :--- | :--- | :--- |
| `journal_mode=WAL` | **Database file** — written once into the header | Required (`RequireWriteAheadLog`) |
| `busy_timeout` | **Connection** | 5000 ms |
| `synchronous` | **Connection** | `NORMAL` |
| `foreign_keys` | **Connection** | `ON` |

Three of those four reset to SQLite's defaults on every new connection. This is why "we set our
pragmas at startup" silently stops being true the moment a connection pool grows past its first
connection — a fresh connection arrives with `foreign_keys` off and no busy timeout. The gate applies
them to every connection it opens, and the EF Core adapter applies them to the connections EF Core
opens for itself.

WAL is not optional by default. Without it a writer blocks every reader, so free concurrent reads —
the library's central promise — quietly stop holding. If WAL cannot be enabled (some network
filesystems), the gate throws rather than degrading in silence. Set `RequireWriteAheadLog = false` to
accept blocking reads knowingly.

## Retries

Even with a queue, SQLite can still report contention: another process holds the file, a WAL
checkpoint collides with a reader, a long-lived read snapshot invalidates a writer. Those are
transient, so they are retried — with exponential backoff and jitter — and nothing else is.

```csharp
options.MaxRetryAttempts = 5;                              // retries after the first attempt
options.InitialRetryDelay = TimeSpan.FromMilliseconds(50); // doubles each time
options.MaxRetryDelay = TimeSpan.FromSeconds(1);           // ceiling for one delay
options.MaxTotalRetryDuration = TimeSpan.FromSeconds(30);  // ceiling for the whole operation
options.RetryJitter = 0.25;                                // ±25%, so writers don't retry in lockstep
```

Only `SQLITE_BUSY` (5) and `SQLITE_LOCKED` (6) are retried. A constraint violation, a syntax error or
a missing table is a real answer and is rethrown immediately. When the budget runs out, the
**original** `SqliteException` is rethrown rather than a wrapper, so existing error handling keeps
working.

### The timeout that would otherwise win

`Microsoft.Data.Sqlite` runs a busy-retry loop of its **own**, in managed code, bounded by
`CommandTimeout` — and its default is 30 seconds. It applies to plain commands and to the `BEGIN`
issued by `BeginTransaction`, and `PRAGMA busy_timeout` does not affect it.

Left alone, that loop sits in front of everything this library configures: a contended write would
block for half a minute inside the provider before the gate's retry policy ever saw a
`SqliteException`. So the gate writes `Default Timeout` into its connection string, following
`BusyTimeout` unless you set `CommandTimeout` explicitly.

Two sharp edges came out of measuring this, and are worth knowing if you tune it yourself: the
setting is a whole number of **seconds**, so sub-second values round up to one; and a value of
**zero means wait forever**, not "do not wait". The gate never writes zero.

## Options

| Option | Default | What it does |
| :--- | :--- | :--- |
| `BusyTimeout` | 5 s | `PRAGMA busy_timeout` on every connection |
| `CommandTimeout` | follows `BusyTimeout` | Bounds the provider's own busy-retry loop |
| `Synchronous` | `Normal` | `PRAGMA synchronous` |
| `EnableForeignKeys` | `true` | `PRAGMA foreign_keys` |
| `RequireWriteAheadLog` | `true` | Fail rather than run without WAL |
| `MaxReaderConnections` | CPU count, clamped 4–16 | Size of the reader pool |
| `MaxRetryAttempts` | 5 | Retries after a contention failure |
| `InitialRetryDelay` / `MaxRetryDelay` | 50 ms / 1 s | Backoff growth and ceiling |
| `MaxTotalRetryDuration` | 30 s | Whole-operation retry budget |
| `RetryJitter` | 0.25 | Randomisation of each backoff |
| `WriteQueueTimeout` | `null` (wait, but cancellably) | Give up queuing after this long |
| `EnableMetrics` | `true` | Report to the `PepperX.SqliteGate` meter |
| `LoggerFactory` | none | Where queue waits and retries are logged |
| `AdditionalPragmas` | empty | Extra pragmas for every connection |
| `TimeProvider` | `TimeProvider.System` | Time source, for deterministic tests |

Options are applied by whichever call **creates** the gate; a later caller naming the same file gets
the existing one. One database cannot have two sets of concurrency settings.

## Observability

Subscribe to the `PepperX.SqliteGate` meter — for example from OpenTelemetry:

| Instrument | Type | Meaning |
| :--- | :--- | :--- |
| `sqlitegate.write_queue.depth` | UpDownCounter | Writes queued or executing |
| `sqlitegate.write.wait.duration` | Histogram (ms) | Time spent queued before the slot was granted |
| `sqlitegate.write.duration` | Histogram (ms) | Time spent executing while holding the slot |
| `sqlitegate.read.duration` | Histogram (ms) | Time spent reading, including pool wait |
| `sqlitegate.retry.count` | Counter | Retries caused by contention |
| `sqlitegate.operation.count` | Counter | Completed operations, tagged read/write and success/failure |

All tagged with `db.name`. `gate.PendingWriters` exposes the same queue depth synchronously.

## Why a semaphore rather than a channel of write requests

Both give mutual exclusion. `SemaphoreSlim(1, 1)` keeps the caller on their own execution path: a
channel would need a pump task, and every write would then be marshalled through a
`TaskCompletionSource`, costing a thread hop per write, rewriting exception stack traces, and losing
the ambient context (activity, culture, async-local state) that callers expect to still be there
inside their delegate. `WaitAsync` already supports cancellation and timeout natively, and its
asynchronous waiters are queued in a linked list drained in arrival order, so ordering is
first-in-first-out in practice.

That last point is an implementation property rather than a documented contract. If you need strict,
guaranteed FIFO — a fair scheduler rather than a fast one — a channel-with-pump is the design worth
paying the overhead for. For serializing writes to a file, it is not.

## A note on threads

SQLite has no asynchronous I/O. `ExecuteNonQueryAsync` and friends do their work on the calling
thread and hand back an already-completed task, so a burst of concurrent gated operations genuinely
occupies that many thread-pool threads for a moment. The pool injects new threads slowly once its
minimum is exhausted, so a service that expects large write bursts may want to raise
`ThreadPool.SetMinThreads`. This repository's own test host does exactly that.

## In-memory databases

An `:memory:` database lives inside its connection: a second connection is a second, empty database.
The gate detects this and serializes reads through the write slot on a single shared connection,
which is correct, and a reminder that concurrency has to be proven on disk — there are no file locks
in memory to get wrong.

## Scope

This is in-process concurrency: many threads or requests inside **one running application** hitting
the same file. That covers the overwhelming majority of "database is locked" reports.

It is **not** a distributed lock manager. If a separate process — a worker, a migration tool, a
`sqlite3` shell — writes to the same file, coordination still falls to SQLite's own OS-level locking
plus `busy_timeout`, and the gate's retry policy backs off and tries again rather than failing on the
first collision. One process cannot serialize another one's writes, and no in-process library can
promise otherwise. See the [root README](https://github.com/PepperX-Dev/SqliteGate#-scope-in-process-concurrency).

## 🤝 Contributing & License

This project is part of the [PepperX Ecosystem](https://github.com/PepperX-Dev).

Licensed under the **MIT License** — see [LICENSE](https://github.com/PepperX-Dev/SqliteGate/blob/main/LICENSE).