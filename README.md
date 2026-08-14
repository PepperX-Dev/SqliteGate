<p align="center">
  <a href="https://github.com/PepperX-Dev"><img src="https://img.shields.io/badge/Part_of-PepperX_Ecosystem-512BD4?style=for-the-badge&logo=github&logoColor=white" alt="Part of PepperX Ecosystem"></a>
</p>

<p align="center">
  <img src="https://raw.githubusercontent.com/PepperX-Dev/SqliteGate/main/icon.png" alt="SqliteGate" width="140" />
</p>

<h1 align="center">The SqliteGate Ecosystem</h1>

<p align="center">
  <strong>Treat SQLite like a real database server.</strong><br/>
  Reads flow freely, writes queue safely — whether you're on raw ADO.NET, Dapper, or EF Core.
</p>

<p align="center">
  <a href="https://github.com/PepperX-Dev/SqliteGate"><img src="https://img.shields.io/badge/GitHub-PepperX--Dev%2FSqliteGate-181717?style=flat-square&logo=github" alt="GitHub"></a>
  <a href="https://www.nuget.org/profiles/AmirHosseinMp02"><img src="https://img.shields.io/badge/NuGet-Profile-0078D4?style=flat-square&logo=nuget" alt="NuGet"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg?style=flat-square" alt="License"></a>
</p>

<br>

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

Nothing is wrong with your code. SQLite allows **many readers but only one writer**, and ADO.NET,
Dapper and EF Core all open connections that know nothing about each other. Two requests, one file,
one loser.

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
[`BaselineWithoutGateTests`](./tests/PepperX.SqliteGate.Tests/BaselineWithoutGateTests.cs) asserts
the first one **fails** and the second one **doesn't**.

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

## 🧩 Which package do I need?

One engine, three front doors. Pick the one that matches how you already write data access — or
install several, because **the gate is keyed on the database file**, so they all queue together
instead of colliding.

| Package | You want this if… | Status | NuGet |
| :--- | :--- | :---: | :--- |
| **[PepperX.SqliteGate](./src/PepperX.SqliteGate)** | You use **raw ADO.NET** (`Microsoft.Data.Sqlite`). Also the **core engine** every other package is built on: the per-file gate registry, single-writer queue, reader pool, automatic pragmas, retry with backoff, logging and metrics. | ✅ Released | [![NuGet](https://img.shields.io/nuget/v/PepperX.SqliteGate?style=flat-square&label=)](https://www.nuget.org/packages/PepperX.SqliteGate) |
| **[PepperX.SqliteGate.Dapper](./src/PepperX.SqliteGate.Dapper)** | You use **Dapper**. The methods you already write, as extensions on the gate: `Query*` reads from the pool, `Execute*` goes through the write queue — routing by method name, never by parsing your SQL. | ✅ Released | [![NuGet](https://img.shields.io/nuget/v/PepperX.SqliteGate.Dapper?style=flat-square&label=)](https://www.nuget.org/packages/PepperX.SqliteGate.Dapper) |
| **[PepperX.SqliteGate.EFCore](./src/PepperX.SqliteGate.EFCore)** | You use **EF Core**. `UseSqlite` becomes `UseSqliteGate`; `SaveChanges`, migrations and `EnsureCreated` are queued while LINQ queries read freely. Works with `AddDbContextPool`. | ✅ Released | [![NuGet](https://img.shields.io/nuget/v/PepperX.SqliteGate.EFCore?style=flat-square&label=)](https://www.nuget.org/packages/PepperX.SqliteGate.EFCore) |

There is no separate `.Ado` package: the core one *is* the ADO.NET experience, built directly on
`Microsoft.Data.Sqlite` — the layer underneath Dapper and EF Core alike.

### One guarantee, whichever API you use

```csharp
// Raw ADO.NET
await gate.WriteAsync(async (connection, ct) => { /* … */ });

// Dapper
await gate.ExecuteAsync("INSERT INTO Todos (Title) VALUES (@Title)", new { Title = "Buy milk" });

// EF Core
db.Todos.Add(new Todo { Title = "Buy milk" });
await db.SaveChangesAsync();
```

All three reach `app.db` through **the same write queue**, because the gate is keyed on the database
file rather than on the library you happened to call it from.

---

## ⚡ At a glance

- 🚪 **One gate per database file, process-wide** — every caller shares one write queue.
- 📝 **Single-writer queue** — one write in flight per file, with observable depth.
- 📖 **Free concurrent reads** — readers work from a WAL snapshot and never wait for a writer.
- ⚙️ **Nothing to configure** — WAL, `synchronous`, `busy_timeout` and `foreign_keys` applied to every connection.
- 🔁 **Retry with backoff and jitter** — for the residual contention a queue cannot remove.
- 📊 **Contention you can see** — queue depth, wait time and retries via `System.Diagnostics.Metrics`.
- 🧵 **Async-first** — `CancellationToken` honoured while queued and while backing off. No sync-over-async.
- 🎯 **Explicit intent** — the library never guesses whether your SQL reads or writes.

---

## 🧪 Does it actually work?

The [sample Web API](./samples/PepperX.SqliteGate.Sample.WebApi) puts all three packages in front of
one SQLite file so you can hammer it yourself. Running the load test in that project's README —
**600 concurrent HTTP requests** against a single `.db`:

```
    300 200          ← inserts, spread across ADO.NET, Dapper and EF Core
    300 200          ← read-modify-write increments against one row

{"total":300,"bySource":[{"source":"ado","count":100},{"source":"dapper","count":100},{"source":"efcore","count":100}]}
{"value":300}
```

Zero `SQLITE_BUSY`. Exactly 100 rows from each package. And the counter reads **exactly 300** — each
increment reads the value then writes back one more, so a single overlap between two writers would
have left it short.

Behind that, **98 tests** run on every build, including a shared conformance suite that puts the
identical 200-writer scenario through all three packages, so no adapter can drift out of the
guarantee on its own. See [tests/README.md](./tests/README.md).

---

## 🎯 Scope: in-process concurrency

SqliteGate solves **in-process** concurrency: many threads or requests inside one running
application or service hitting the same SQLite file. That covers the overwhelming majority of
"database is locked" reports, because the overwhelming majority of them come from one process
competing with itself.

It is **not** a distributed lock manager. If a second process — a worker, a migration tool, a
`sqlite3` shell — writes to the same file, coordination between them still falls to SQLite's own
OS-level file locking plus `busy_timeout`. SqliteGate handles that case as gracefully as it can:
the pragmas are set for you, and the retry policy backs off and tries again rather than failing on
the first collision. But one process cannot serialize another one's writes, and no in-process
library can promise otherwise.

---

## 🏛️ Architecture & Philosophy

SqliteGate is built around a core set of engineering principles:

*   **🚪 Single Writer, Many Readers:** One write is in flight per database file at a time; everyone
    else waits in a queue instead of colliding at the file. Reads never join that queue — under WAL
    they work from a snapshot, so a long write cannot stall a single reader.
*   **⚙️ Nothing to Configure by Hand:** `journal_mode=WAL`, `synchronous=NORMAL`, `busy_timeout` and
    `foreign_keys` are applied on first use — and re-applied to **every** connection, because three of
    those four are connection-scoped and quietly reset themselves the moment a pool grows.
*   **🧩 One Guarantee, Every API:** ADO.NET, Dapper and EF Core are thin adapters over one engine, not
    three implementations of the same idea. A shared conformance suite runs the identical concurrency
    scenario through all three on every build, so no adapter can drift out of the guarantee alone.
*   **🎯 Explicit Intent, Never Inference:** The library never parses your SQL to decide whether it is a
    read or a write. That guess is wrong for real statements — `INSERT … RETURNING` reads, a `SELECT`
    over a virtual table can write — and being wrong means silently losing the guarantee. You say
    which you meant.
*   **🔍 Contention You Can See:** Queue depth, write wait time and retry counts are published through
    `System.Diagnostics.Metrics`, because contention is the one failure mode a stack trace cannot show
    you.
*   **🔄 Automated CI/CD:** All packages are built, tested, and published via GitHub Actions using OIDC
    Trusted Publishing.

---

## 📂 Repository Structure

This repository is an **Umbrella Monorepo**.

*   **`/src`**: Contains the source code for all SqliteGate libraries. Each library folder contains its own dedicated `README.md` with deep-dive technical documentation, C# examples, and API references.
*   **`/tests`**: Comprehensive `xUnit` test suites, including the concurrency stress suites that prove the guarantee. See [tests/README.md](./tests/README.md).
*   **`/samples`**: A runnable ASP.NET Core Minimal API demonstrating all three packages against one file, with a copy-pasteable load test.

---

## 🤝 Contributing & License

Contributions, issues, and feature requests are welcome!

Unless otherwise specified, all packages in SqliteGate are licensed under the **MIT License** — see the [LICENSE](LICENSE) file for details.

<br>

<p align="center">
  <sub>Engineered with ❤️ and C# by <a href="https://github.com/PepperX-Dev">PepperX-Dev</a></sub>
</p>