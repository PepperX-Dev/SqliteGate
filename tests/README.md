# Testing SqliteGate

```bash
dotnet test SqliteGate.slnx -c Release
```

Everything runs with no setup: there is no external database to start, because the database under
test is a file the suite creates in a temporary directory and deletes afterwards.

## What runs where

| Project | What it covers |
| :--- | :--- |
| `PepperX.SqliteGate.Conformance` | **Not a test project.** The shared concurrency suite every package must satisfy, plus the harness and schema they share |
| `PepperX.SqliteGate.Tests` | The engine: registry, pragmas, write queue, reader pool, retries, cancellation, metrics, options — and the baseline that reproduces the problem |
| `PepperX.SqliteGate.Dapper.Tests` | The shared suite through Dapper, plus the `Query*`/`Execute*` routing rule |
| `PepperX.SqliteGate.EFCore.Tests` | The shared suite through EF Core, plus pooling, change tracking, migrations and gated transactions |

## The shared suite

`ConcurrencyConformanceTests` lives in the conformance project and is inherited by each package, so a
regression shows up on all three at once instead of one adapter quietly drifting out of the guarantee
while its own tests stay green. Adding a package means writing one subclass with six small overrides.

Three scenarios, run identically against raw ADO.NET, Dapper and EF Core:

- **200 concurrent writers.** Zero `SQLITE_BUSY`/`SQLITE_LOCKED`, exactly 200 rows, and the sum of the
  ordinals matches — so the right rows landed, not merely the right number of them.
- **200 concurrent read-modify-writes.** A counter read and written back one higher. SQL cannot make
  that atomic, so any overlap between two writers loses an update and the final value is wrong. It
  ends on exactly 200.
- **200 writers and 200 readers together.** No contention errors, and every reader saw a count that
  was possible at some committed instant.

Each run is started from a common gate so the tasks genuinely collide, and each is bounded by a
timeout — a suite that only finishes because the deadlock guard fired has not passed.

## Tests that would pass whether or not the library worked

Two things are deliberate here.

**Real files, not `:memory:`.** File locking *is* the subject. An in-memory database has no locks to
contend for, so a concurrency suite built on one proves nothing. `:memory:` appears only in
`InMemoryDatabaseTests`, which covers the different behaviour the gate deliberately adopts there.

**A baseline that fails.** `BaselineWithoutGateTests` runs the same concurrent load through plain
`Microsoft.Data.Sqlite` with no gate and asserts that `SQLITE_BUSY` **does** occur, then runs it
through the gate and asserts it does not. Without it, a green suite could mean the library works or
could mean the load was never heavy enough to matter.

Making that failure prompt rather than eventual took some care, and the finding generalises:

> `Microsoft.Data.Sqlite` runs a busy-retry loop of its own, in managed code, bounded by
> `CommandTimeout` — 30 seconds by default. It applies to plain commands and to the `BEGIN` issued by
> `BeginTransaction`, and `PRAGMA busy_timeout` does not affect it. A value of **zero means wait
> forever**, not "do not wait" — a probe with `Default Timeout=0` hung indefinitely rather than
> failing fast.

The baseline shrinks that budget to one second and gives the writers more work than fits in it. The
library takes the same finding seriously in production: the gate writes `Default Timeout` into its
connection string, following `BusyTimeout`, so that the provider's invisible 30-second loop does not
sit in front of everything the caller configured.

## Deterministic contention

`WriteLockBlocker` opens a second connection outside the gate and holds the write lock with
`BEGIN IMMEDIATE`. That is exactly the situation the retry policy exists for — something the gate does
not control holding the file — and it makes the retry tests deterministic rather than hopeful:
attempt counts, backoff growth, jitter bounds, cancellation during backoff, and that non-contention
errors are never retried.

## Timing assertions

Concurrency suites are where flaky tests come from, so assertions are about ordering wherever
ordering is what matters. The cancellation test does not measure how quickly a cancelled write
returns; it asserts that the write left the queue *while the holder still held it*, which is the real
invariant and cannot be affected by a loaded CI runner. Where a duration is genuinely the subject —
"a cancelled operation must not sit out a thirty-second backoff" — the bound is set far enough below
the behaviour being excluded that scheduling noise cannot reach it.

One accommodation is made to the test host, in `TestHostConfiguration`: the thread pool's minimum is
raised before any suite runs. SQLite has no asynchronous I/O, so `ExecuteNonQueryAsync` does its work
on the calling thread; 200 concurrent writers genuinely occupy 200 pool threads for a moment, and the
pool injects new threads at roughly one or two per second once its minimum is exhausted. Without the
floor, the suites spend their time queued behind thread injection and any assertion about promptness
becomes an assertion about the scheduler. This is worth knowing about production code too, and is
noted in the core README.

## Coverage

`coverlet.collector` is referenced by every test project:

```bash
dotnet test SqliteGate.slnx -c Release --collect:"XPlat Code Coverage"
```

CI runs the same command and uploads the resulting Cobertura reports as a build artifact.

## Continuous integration

`.github/workflows/build-and-test.yml` restores, builds in Release with warnings as errors, and runs
the full suite — including the concurrency stress tests — on every push and pull request to `main`.
`.github/workflows/publish-nuget.yml` re-runs the same tests on a release tag before packing, so a
version cannot be published without having survived the load it claims to handle.
