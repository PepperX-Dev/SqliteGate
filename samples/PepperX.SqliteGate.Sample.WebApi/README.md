# PepperX.SqliteGate Sample API

A .NET 10 Minimal API where **one SQLite file** is reached through **all three packages at once** —
raw ADO.NET, Dapper, and Entity Framework Core — sharing a single write queue.

The point of the sample is to be hammered.

## Run it

```bash
dotnet run --project samples/PepperX.SqliteGate.Sample.WebApi
```

Then open `http://localhost:5185/openapi/v1.json`, or use [`Requests.http`](./Requests.http) from
Visual Studio, VS Code (REST Client) or Rider.

The database file (`sample.db`) and its schema are created on first run, through the gate. There is
nothing to install and nothing to configure.

## Endpoints

| Endpoint | What it does |
| :--- | :--- |
| `POST /api/todos/ado` | Insert through raw ADO.NET (`gate.WriteAsync`) |
| `POST /api/todos/dapper` | Insert through Dapper (`gate.ExecuteScalarAsync`) |
| `POST /api/todos/efcore` | Insert through EF Core (`SaveChangesAsync`) |
| `GET /api/todos` | List recent todos — a read, never queued |
| `GET /api/todos/count` | Totals, broken down by which package wrote each row |
| `POST /api/counter/increment` | Read a counter and write back one more — the lost-update shape |
| `GET /api/counter` | Read the counter |
| `GET /api/gate` | Live queue depth, journal mode, and the gate's settings |
| `DELETE /api/gate/data` | Reset between runs |

## Load test

Any HTTP load generator works. This needs nothing but `curl` and `xargs`:

```bash
BASE=http://localhost:5185

curl -s -X DELETE $BASE/api/gate/data

# 300 concurrent inserts, spread across all three packages
seq 1 300 | xargs -P 100 -I{} sh -c '
  case $(( {} % 3 )) in
    0) p=ado ;; 1) p=dapper ;; 2) p=efcore ;;
  esac
  curl -s -o /dev/null -w "%{http_code}\n" -X POST "'"$BASE"'/api/todos/$p" \
       -H "Content-Type: application/json" -d "{\"title\":\"todo {}\"}"
' | sort | uniq -c

# 300 concurrent read-modify-writes against a single row
seq 1 300 | xargs -P 100 -I{} \
  curl -s -o /dev/null -w '%{http_code}\n' -X POST $BASE/api/counter/increment | sort | uniq -c

curl -s $BASE/api/todos/count
curl -s $BASE/api/counter
```

### What you should see

```
    300 200          ← every insert succeeded
    300 200          ← every increment succeeded

{"total":300,"bySource":[{"source":"ado","count":100},{"source":"dapper","count":100},{"source":"efcore","count":100}]}
{"value":300}
```

Three things are worth noticing.

**No `500`s.** Not one `SQLITE_BUSY`, from 600 concurrent requests against a single file through
three different data access libraries.

**The counter is exactly 300.** Each increment reads the value and writes back one more, which is the
classic lost-update shape — if two writers ever overlapped, the total would come out short.

**The per-package counts are exact.** All three APIs wrote to the same file through the same queue.

With `hey` or `wrk` instead:

```bash
hey -n 2000 -c 200 -m POST -H 'Content-Type: application/json' \
    -d '{"title":"load"}' $BASE/api/todos/dapper
```

## Watching contention happen

While a load test is running, poll the gate in another terminal:

```bash
watch -n 0.2 'curl -s http://localhost:5185/api/gate'
```

```json
{
  "databasePath": "/…/sample.db",
  "pendingWriters": 37,
  "journalMode": "wal",
  "busyTimeout": "00:00:05",
  "maxRetryAttempts": 5,
  "maxReaderConnections": 4,
  "gatesInProcess": 1
}
```

`pendingWriters` rising is the queue doing its job: those are requests waiting their turn instead of
colliding at the file. `gatesInProcess: 1` is the other half of the story — three packages, one gate.

The application log shows the same thing from the inside (the sample sets `PepperX.SqliteGate` to
`Debug` in development):

```
SqliteGate bootstrapped '/…/sample.db': journal_mode=wal, synchronous=Normal, busy_timeout=5000ms, foreign_keys=True.
Write on '/…/sample.db' waited 80ms behind 4 other write(s).
```

## Try it without the gate

To see what the gate is preventing, take it out: swap `gate.WriteAsync(...)` in the `/api/todos/ado`
endpoint for a bare `new SqliteConnection(...)` that opens, inserts and closes. Re-run the load test
and the `500`s appear almost immediately.

The repository does this properly, as a test rather than an exercise — see
[`BaselineWithoutGateTests`](../../tests/PepperX.SqliteGate.Tests/BaselineWithoutGateTests.cs).
