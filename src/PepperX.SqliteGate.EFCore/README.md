[![Part of PepperX Ecosystem](https://img.shields.io/badge/Part_of-PepperX_Ecosystem-512BD4?style=for-the-badge&logo=github&logoColor=white)](https://github.com/PepperX-Dev)

![PepperX.SqliteGate.EFCore Logo](https://raw.githubusercontent.com/PepperX-Dev/SqliteGate/main/icon.png)

# PepperX.SqliteGate.EFCore

[![NuGet Version](https://img.shields.io/nuget/v/PepperX.SqliteGate.EFCore.svg?style=flat-square&label=PepperX.SqliteGate.EFCore)](https://www.nuget.org/packages/PepperX.SqliteGate.EFCore/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg?style=flat-square)](https://github.com/PepperX-Dev/SqliteGate/blob/main/LICENSE)

> **`SaveChangesAsync` throwing `database is locked` on SQLite?**
> Change `UseSqlite` to `UseSqliteGate`. That's the migration.

---

## 😖 The problem

EF Core hides the connection from you. It cannot hide that SQLite takes one writer at a time.

```csharp
services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=app.db"));

// …and then, from 200 concurrent requests:
db.Todos.Add(new Todo { Title = "Buy milk" });
await db.SaveChangesAsync();
```

```
Microsoft.Data.Sqlite.SqliteException (0x80004005): SQLite Error 5: 'database is locked'.
```

Each scoped `DbContext` gets its own connection. Under load they arrive together, and SQLite lets
exactly one of them write. The failure looks random, gets worse with traffic, and never reproduces
on your laptop.

## ✅ The fix

```csharp
services.AddDbContext<AppDbContext>(options =>
    options.UseSqliteGate("app.db"));      // ← the whole change

db.Todos.Add(new Todo { Title = "Buy milk" });
await db.SaveChangesAsync();               // queued behind one write slot

var todos = await db.Todos.ToListAsync();  // reads freely, never queued
```

Your entities, your LINQ, your change tracking, your migrations — all untouched. `SaveChanges` now
waits its turn instead of colliding, and queries keep reading from a WAL snapshot while it does.

---

## 🤔 Why this exists

EF Core is the hardest of the three adapters, and the reason is worth 20 seconds of your time:
**you cannot wrap EF Core from the outside.** It owns its connections, its transactions, and its
migration pipeline, so a lock around `SaveChangesAsync()` in your service layer misses everything
else EF does — and misses every *other* library touching the same file.

So this package hooks three specific seams:

| Seam | Covers |
| :--- | :--- |
| `ISaveChangesInterceptor` | `SaveChanges` / `SaveChangesAsync`, both sync and async paths |
| `IMigrator` decorator | `Database.Migrate()` — including the lock EF takes *before* running anything |
| `IMigrationCommandExecutor` decorator | `Database.EnsureCreated()` |

That middle row was a genuine discovery. Gating the migration *commands* wasn't enough:
`Migrate()` still failed with `database is locked`, because EF Core acquires a **migration lock**
through the history repository *above* the command executor — outside the queue entirely. Migrations
are now gated one level further out. The full story is in
[Migrations needed a second seam](#migrations-needed-a-second-seam-and-finding-out-why-was-the-interesting-part).

And because the gate is keyed on the **database file**, your `DbContext` shares one write slot with
any Dapper or raw ADO.NET code in the same process — not a second, separate queue that collides with
the first.

---

## 🎯 Is this the package you need?

| How you talk to SQLite | Install |
| :--- | :--- |
| **EF Core** | **This package** ✅ |
| Raw ADO.NET (`Microsoft.Data.Sqlite`) | [`PepperX.SqliteGate`](https://github.com/PepperX-Dev/SqliteGate/tree/main/src/PepperX.SqliteGate) |
| Dapper | [`PepperX.SqliteGate.Dapper`](https://github.com/PepperX-Dev/SqliteGate/tree/main/src/PepperX.SqliteGate.Dapper) |
| A mix of them | Install each — one gate per file, shared automatically |

**Skip it if:** you only ever write from one thread, or you need locking coordinated across
*separate processes*.

**Read [Trade-offs](#trade-offs-collected) before adopting** — two things (`ExecuteUpdate`/
`ExecuteDelete`, and read-modify-write) need one extra line from you, and this README says so plainly
rather than letting you find out under load.

---

## ⚡ At a glance

- 🔁 **One line to adopt** — `UseSqlite(…)` becomes `UseSqliteGate(…)`.
- 💾 **`SaveChanges` is queued** — async *and* sync paths, so nothing slips past.
- 🏗️ **Migrations and `EnsureCreated` are queued too** — schema changes are writes.
- 📖 **LINQ queries never queue** — reads run free under WAL.
- 🧩 **Works with `AddDbContextPool`** — no per-context state in the interceptors.
- 🧠 **Change tracking untouched** — interception happens above it.
- 🔒 **`BeginGatedTransactionAsync`** — hold the write slot across several saves, safely.
- 🔗 **Shares one gate** with the ADO.NET and Dapper packages, keyed on the file.

## Install

```bash
dotnet add package PepperX.SqliteGate.EFCore
```

## Setup

```csharp
// By path — creates or joins the gate for that file
services.AddDbContext<AppDbContext>(options => options.UseSqliteGate("app.db"));

// Or against a gate already registered, so DI hands out one instance everywhere
services.AddSqliteGate("app.db");
services.AddDbContext<AppDbContext>((provider, options) =>
    options.UseSqliteGate(provider.GetRequiredService<ISqliteGate>()));
```

Either way the gate is keyed on the database file, so a `DbContext`, a Dapper repository and a raw
ADO.NET call against `app.db` all queue behind one write slot. `db.Database.GetSqliteGate()` returns
it if you need it directly.

`AddDbContextPool` is supported.

## What is gated, and where

| Operation | Seam | Gated |
| :--- | :--- | :---: |
| `SaveChanges` / `SaveChangesAsync` | `ISaveChangesInterceptor` | ✅ |
| `Database.Migrate()` / `MigrateAsync()` | `IMigrator` decorator | ✅ |
| `Database.EnsureCreated()` | `IMigrationCommandExecutor` decorator | ✅ |
| LINQ queries | — | Not gated, by design: reads never queue |
| `ExecuteUpdate` / `ExecuteDelete` | — | ⚠️ Not gated — see below |
| Raw `Database.ExecuteSqlAsync` | — | ⚠️ Not gated — see below |

### Migrations needed a second seam, and finding out why was the interesting part

The obvious place to gate schema changes is `IMigrationCommandExecutor`: every command a migration
runs passes through it, and so does `EnsureCreated`. That was the first implementation, and the
`EnsureCreated` test passed immediately.

`Migrate()` did not. It failed with `SQLite Error 5: 'database is locked'` — the exact error the
library exists to prevent — while the gate reported that no lease had been taken at all.

The cause: before EF Core executes any migration command, it takes a **migration lock** on the
database through the history repository, so two processes cannot migrate at once. That happens
*above* the command executor, so it reached SQLite outside the queue, met a writer holding the file,
and failed on the spot rather than waiting its turn.

So migrations are gated at `IMigrator` instead, which is far enough out to cover the lock, the
history table and every command as one unit. The command-executor decorator stays for
`EnsureCreated`, which never goes through a migrator. When both are in play the inner one sees the
slot is already held and leaves it alone.

### Decorating rather than replacing

`optionsBuilder.ReplaceService<TService, TImplementation>()` is the documented way to swap an EF Core
service, but it *substitutes* a registration — it gives no way to reach the implementation being
replaced. Constructing that implementation yourself means naming it, and every one of these lives in
EF Core's `Internal` namespace with no compatibility promise across versions. The first draft of this
package did exactly that, and needed an `EF1001` suppression to compile.

Instead, the options extension reads the existing `ServiceDescriptor` during `ApplyServices` and
rebuilds the original through `ActivatorUtilities`. Whatever the SQLite provider registered is what
gets constructed, including through any future change to its constructor; the decorator only has to
know the public interface it is wrapping. No internal type is named anywhere in this package.

## Reads: an honest note on the reader pool

The core engine keeps a bounded pool of reader connections. **EF Core does not use it**, and cannot:
EF Core opens and manages its own connections from a connection string, and there is no supported
seam for handing it a connection the gate owns per query without breaking its connection lifetime
model.

What actually happens is that EF Core opens its own connections from the gate's connection string,
and `Microsoft.Data.Sqlite` pools them internally. The practical result is the same in the way that
matters — **reads never queue behind the writer**, because that comes from WAL, not from whose pool
the connection came out of. The differences are that `MaxReaderConnections` does not bound EF Core's
concurrency, and that EF Core's reads do not appear in the `sqlitegate.read.duration` metric.

A `IDbConnectionInterceptor` applies the gate's connection-scoped pragmas (`busy_timeout`,
`synchronous`, `foreign_keys`) to every connection EF Core opens, so those still match the rest of
the application — which matters more than it sounds, since all three reset to SQLite's defaults on
each new connection.

## Read-modify-write needs a gated transaction

`SaveChanges` takes the write slot for the duration of the save. The read that decided *what* to save
happened before that — so two contexts can both read `5` and both save `6`, and one increment
disappears. This is not a SQLite problem and the gate cannot fix it from inside `SaveChanges`; the
read has to be inside the same held slot.

```csharp
await using var transaction = await db.Database.BeginGatedTransactionAsync();

var counter = await db.Counters.SingleAsync(c => c.Name == "hits");
counter.Value += 1;

await db.SaveChangesAsync();
await transaction.CommitAsync();
```

`BeginGatedTransactionAsync` takes the write slot **first**, then opens the EF Core transaction, and
holds both until disposal. It is also the right tool for several saves that must commit together: a
plain `BeginTransactionAsync` would let the slot be taken and released around each individual save
while the SQLite write lock stayed held by the open transaction, so other writers would be let out of
the queue only to meet a locked database.

While a gated transaction is held, `SaveChangesAsync` on the same context will not try to take the
slot again. It cannot — the slot is a non-reentrant semaphore, so a second attempt would deadlock the
application on its own first write. The adapter tracks which contexts hold the slot and by which
route, which is what makes the two compose.

## Not gated: ExecuteUpdate, ExecuteDelete and raw SQL

`ExecuteUpdateAsync`, `ExecuteDeleteAsync` and `Database.ExecuteSqlAsync` bypass the change tracker
and go straight to the database, so no interceptor sees them as writes. EF Core exposes them through
the same command pipeline as queries, and there is no reliable way to tell one from the other without
parsing SQL — which this library will not do, for the reasons in the
[core README](https://github.com/PepperX-Dev/SqliteGate/blob/main/src/PepperX.SqliteGate/README.md#read-and-write-intent-is-always-explicit).

Wrap them when you use them:

```csharp
await using var transaction = await db.Database.BeginGatedTransactionAsync();
await db.Todos.Where(t => t.Done).ExecuteDeleteAsync();
await transaction.CommitAsync();
```

They are not silently broken without it — the pragmas and `busy_timeout` still apply, and SQLite will
usually wait rather than fail. But they are outside the queue, and under real contention that is a
difference you can measure.

## Sync `SaveChanges` is supported

Both the asynchronous and synchronous save paths are intercepted. Leaving the synchronous one out
would be tidier, and would mean a single `SaveChanges()` call anywhere in the application bypassing
the queue entirely — a guarantee with a hole in it is not a guarantee. The synchronous path takes the
slot with a genuine blocking wait rather than by waiting on the asynchronous one inline.

## Pooling and change tracking

The interceptors hold no per-context state, which is what makes them safe to share across a pooled
`DbContext`. Lease bookkeeping is keyed on the context instance and released on every completion path
— committed, thrown, or cancelled — because a leaked lease would hold the process-wide write slot
forever, and with pooling it would come back attached to the next request that reuses the instance.
The suite runs 100 concurrent saves through `AddDbContextPool` and asserts the queue drains to zero.

Interception happens above the change tracker, so nothing here touches tracked state, generated keys,
or entity states.

## Trade-offs, collected

| Trade-off | Why | Impact |
| :--- | :--- | :--- |
| EF Core reads use the provider's connection pool, not the gate's | No supported seam to inject per-query connections | `MaxReaderConnections` does not bound EF Core; its reads are not in the read metric. Reads are still never blocked. |
| `ExecuteUpdate`/`ExecuteDelete`/raw SQL are not auto-gated | Indistinguishable from queries without parsing SQL | Wrap in `BeginGatedTransactionAsync` |
| Read-modify-write needs an explicit gated transaction | The read precedes the save the interceptor sees | One extra line where it matters |
| Two decoration seams for schema changes | The migration lock is taken above the command executor | None at runtime; more surface to maintain |
| Gated transactions cannot nest | The write slot is non-reentrant | Throws a clear exception instead of deadlocking |

## Going deeper

Configuration (busy timeout, retry policy, WAL setup), the metrics, and the one-gate-per-file rule
are all documented in the core package: [`PepperX.SqliteGate`](https://github.com/PepperX-Dev/SqliteGate/tree/main/src/PepperX.SqliteGate).

## 🤝 Contributing & License

This project is part of the [PepperX Ecosystem](https://github.com/PepperX-Dev).

Licensed under the **MIT License** — see [LICENSE](https://github.com/PepperX-Dev/SqliteGate/blob/main/LICENSE).