using Microsoft.EntityFrameworkCore;
using PepperX.SqliteGate;
using PepperX.SqliteGate.Dapper;
using PepperX.SqliteGate.DependencyInjection;
using PepperX.SqliteGate.EFCore;

namespace PepperX.SqliteGate.Sample.WebApi;

/// <summary>
/// A Minimal API backed by one SQLite file, reachable through all three packages at once.
/// </summary>
/// <remarks>
/// Point a load generator at any of the write endpoints — or all of them together — and watch for
/// "database is locked". There should not be one. The <c>/api/gate</c> endpoint shows the queue
/// while it happens.
/// </remarks>
public class Program
{
    /// <summary>Entry point.</summary>
    /// <param name="args">Command line arguments.</param>
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        var databasePath = builder.Configuration["SqliteGate:DatabasePath"]
            ?? Path.Combine(builder.Environment.ContentRootPath, "sample.db");

        builder.Services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, context, cancellationToken) =>
            {
                document.Info.Title = "PepperX.SqliteGate Sample API";
                document.Info.Description = """
                    # 🚪 Welcome to PepperX.SqliteGate

                    Every endpoint below reads and writes **the same SQLite file**, through three
                    different APIs — raw ADO.NET, Dapper, and Entity Framework Core — and all of them
                    share one write queue underneath.

                    ### The point of the sample

                    Hammer the write endpoints concurrently. A plain SQLite-backed service would start
                    returning `SQLITE_BUSY` / "database is locked" almost immediately. Here the writes
                    queue instead, the reads never wait, and the totals still add up.

                    * `POST /api/todos/ado` · `POST /api/todos/dapper` · `POST /api/todos/efcore`
                      — the same insert, one per package. Mix them freely.
                    * `POST /api/counter/increment` — a read-modify-write. If writes were not
                      serialized this is where updates would go missing.
                    * `GET /api/gate` — live queue depth, so you can watch contention build.

                    ### Prerequisites

                    None. The schema is created at startup, through the gate, and the database file is
                    made for you on first run.

                    See this project's README.md for a copy-pasteable load test.
                    """;

                return Task.CompletedTask;
            });
        });

        // One gate for the file, registered once. Everything below resolves to this same instance —
        // including the DbContext, which is the whole point.
        builder.Services.AddSqliteGate(databasePath, options =>
        {
            options.BusyTimeout = TimeSpan.FromSeconds(5);
            options.MaxRetryAttempts = 5;
        });

        builder.Services.AddDbContext<TodoDbContext>((provider, options) =>
            options.UseSqliteGate(provider.GetRequiredService<ISqliteGate>()));

        var app = builder.Build();

        await SampleDatabase.InitializeAsync(app.Services);

        if (app.Environment.IsDevelopment())
        {
            // Exposes /openapi/v1.json — consumed by .http files, Postman, or frontend codegen.
            app.MapOpenApi();
        }

        // ==========================================
        // GROUP 1: WRITES — one endpoint per package
        // ==========================================
        var writes = app.MapGroup("/api/todos").WithTags("1. Writes (one per package)");

        writes.MapPost("/ado", async (CreateTodo request, ISqliteGate gate, CancellationToken cancellationToken) =>
        {
            var id = await gate.WriteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "INSERT INTO Todos (Title, Source) VALUES ($title, 'ado') RETURNING Id;";
                    command.Parameters.AddWithValue("$title", request.Title);
                    return (long)(await command.ExecuteScalarAsync(token))!;
                },
                cancellationToken);

            return Results.Ok(new { id, source = "ado" });
        })
        .WithSummary("Insert through raw ADO.NET")
        .WithDescription("Goes through ISqliteGate.WriteAsync — the write queue, inside a transaction.");

        writes.MapPost("/dapper", async (CreateTodo request, ISqliteGate gate, CancellationToken cancellationToken) =>
        {
            var id = await gate.ExecuteScalarAsync<long>(
                "INSERT INTO Todos (Title, Source) VALUES (@Title, 'dapper') RETURNING Id;",
                new { request.Title },
                cancellationToken: cancellationToken);

            return Results.Ok(new { id, source = "dapper" });
        })
        .WithSummary("Insert through Dapper")
        .WithDescription("Execute* routes to the write queue. Query* would have gone to the reader pool.");

        writes.MapPost("/efcore", async (CreateTodo request, TodoDbContext db, CancellationToken cancellationToken) =>
        {
            var todo = new Todo { Title = request.Title, Source = "efcore" };
            db.Todos.Add(todo);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(new { id = todo.Id, source = "efcore" });
        })
        .WithSummary("Insert through EF Core")
        .WithDescription("SaveChangesAsync is intercepted and queued behind the same write slot.");

        // ==========================================
        // GROUP 2: READS — never queued
        // ==========================================
        var reads = app.MapGroup("/api/todos").WithTags("2. Reads (never queued)");

        reads.MapGet("/", async (ISqliteGate gate, int take, CancellationToken cancellationToken) =>
            Results.Ok(await gate.QueryAsync<TodoRow>(
                "SELECT Id, Title, Source FROM Todos ORDER BY Id DESC LIMIT @Take;",
                new { Take = take <= 0 ? 20 : take },
                cancellationToken: cancellationToken)))
        .WithSummary("List the most recent todos")
        .WithDescription("A Dapper Query*, so it runs on a pooled reader connection and never waits for a writer.");

        reads.MapGet("/count", async (ISqliteGate gate, CancellationToken cancellationToken) =>
        {
            var bySource = await gate.QueryAsync<SourceCount>(
                "SELECT Source, COUNT(*) AS Count FROM Todos GROUP BY Source;",
                cancellationToken: cancellationToken);

            var total = await gate.QuerySingleAsync<long>("SELECT COUNT(*) FROM Todos;", cancellationToken: cancellationToken);

            return Results.Ok(new { total, bySource });
        })
        .WithSummary("Count todos, broken down by which package wrote them");

        // ==========================================
        // GROUP 3: THE LOST-UPDATE TEST
        // ==========================================
        var counter = app.MapGroup("/api/counter").WithTags("3. Read-modify-write");

        counter.MapPost("/increment", async (ISqliteGate gate, CancellationToken cancellationToken) =>
        {
            // Read then write, inside one gated write. Fire a thousand of these concurrently and the
            // counter must end up at exactly a thousand — that is only true because the gate holds
            // the write slot across both statements.
            var value = await gate.WriteAsync(
                async (connection, token) =>
                {
                    await using var read = connection.CreateCommand();
                    read.CommandText = "SELECT Value FROM Counters WHERE Name = 'hits';";
                    var current = (long)(await read.ExecuteScalarAsync(token))!;

                    await using var write = connection.CreateCommand();
                    write.CommandText = "UPDATE Counters SET Value = $value WHERE Name = 'hits';";
                    write.Parameters.AddWithValue("$value", current + 1);
                    await write.ExecuteNonQueryAsync(token);

                    return current + 1;
                },
                cancellationToken);

            return Results.Ok(new { value });
        })
        .WithSummary("Increment a counter by reading it and writing back one more")
        .WithDescription("The classic lost-update shape. After N concurrent calls the value must be exactly N.");

        counter.MapGet("/", async (ISqliteGate gate, CancellationToken cancellationToken) =>
            Results.Ok(new
            {
                value = await gate.QuerySingleAsync<long>(
                    "SELECT Value FROM Counters WHERE Name = 'hits';",
                    cancellationToken: cancellationToken),
            }))
        .WithSummary("Read the counter");

        // ==========================================
        // GROUP 4: WHAT THE GATE IS DOING
        // ==========================================
        var diagnostics = app.MapGroup("/api/gate").WithTags("4. Diagnostics");

        diagnostics.MapGet("/", async (ISqliteGate gate, CancellationToken cancellationToken) =>
        {
            var journalMode = await gate.ReadAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "PRAGMA journal_mode;";
                    return (await command.ExecuteScalarAsync(token))?.ToString();
                },
                cancellationToken);

            return Results.Ok(new
            {
                databasePath = gate.DatabasePath,
                pendingWriters = gate.PendingWriters,
                journalMode,
                busyTimeout = gate.Options.BusyTimeout,
                maxRetryAttempts = gate.Options.MaxRetryAttempts,
                maxReaderConnections = gate.Options.MaxReaderConnections,
                gatesInProcess = SqliteGateRegistry.Count,
            });
        })
        .WithSummary("Live view of the gate")
        .WithDescription("Poll this during a load test to watch pendingWriters rise and fall.");

        diagnostics.MapDelete("/data", async (ISqliteGate gate, CancellationToken cancellationToken) =>
        {
            await gate.ExecuteBatchAsync(
                async (connection, transaction, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM Todos; UPDATE Counters SET Value = 0 WHERE Name = 'hits';";
                    await command.ExecuteNonQueryAsync(token);
                },
                cancellationToken);

            return Results.Ok(new { reset = true });
        })
        .WithSummary("Reset the sample data between load tests");

        await app.RunAsync();
    }
}

/// <summary>Request body for creating a todo.</summary>
/// <param name="Title">What to do.</param>
public sealed record CreateTodo(string Title);

/// <summary>A todo as returned by the read endpoints.</summary>
/// <param name="Id">Primary key.</param>
/// <param name="Title">What to do.</param>
/// <param name="Source">Which package wrote it.</param>
public sealed record TodoRow(long Id, string Title, string Source);

/// <summary>A per-package tally.</summary>
/// <param name="Source">Which package wrote the rows.</param>
/// <param name="Count">How many.</param>
public sealed record SourceCount(string Source, long Count);
