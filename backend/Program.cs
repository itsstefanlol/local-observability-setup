using System.Diagnostics;
using Npgsql;
using Prometheus;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "dotnet-backend";

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource =>
    {
        resource.AddService(serviceName: serviceName);
    })
    .WithTracing(tracing =>
    {
        tracing
            .AddAspNetCoreInstrumentation(options =>
            {
                options.RecordException = true;
            })
            .AddHttpClientInstrumentation()
            .AddNpgsql()
            .AddOtlpExporter();
    });

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Missing PostgreSQL connection string");

var slowQueryThresholdMs = builder.Configuration.GetValue<int>("SlowQueryThresholdMs", 250);

var maxPoolSize = ExtractMaxPoolSize(connectionString, fallback: 20);

var dbQueryDuration = Metrics.CreateHistogram(
    "backend_db_query_duration_seconds",
    "Database query duration measured inside the .NET backend.",
    new HistogramConfiguration
    {
        LabelNames = new[] { "query_name" },
        Buckets = Histogram.ExponentialBuckets(0.001, 2, 14)
    });

var dbQueriesTotal = Metrics.CreateCounter(
    "backend_db_queries_total",
    "Total database queries executed by the .NET backend.",
    new CounterConfiguration
    {
        LabelNames = new[] { "query_name", "status" }
    });

var dbSlowQueriesTotal = Metrics.CreateCounter(
    "backend_db_slow_queries_total",
    "Total slow database queries detected by the .NET backend.",
    new CounterConfiguration
    {
        LabelNames = new[] { "query_name" }
    });

var dbQueryErrorsTotal = Metrics.CreateCounter(
    "backend_db_query_errors_total",
    "Total database query errors detected by the .NET backend.",
    new CounterConfiguration
    {
        LabelNames = new[] { "query_name" }
    });

var dbActiveConnections = Metrics.CreateGauge(
    "backend_db_active_connections",
    "Active database connections opened by the backend application.");

var dbMaxPoolSize = Metrics.CreateGauge(
    "backend_db_max_pool_size",
    "Maximum configured PostgreSQL connection pool size used by the backend.");

var dbHealthStatus = Metrics.CreateGauge(
    "backend_db_health_status",
    "Database health from backend perspective. 1 means healthy, 0 means unhealthy.");

dbMaxPoolSize.Set(maxPoolSize);

var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
await using var dataSource = dataSourceBuilder.Build();

var app = builder.Build();

app.UseHttpMetrics();

await InitializeDatabase(dataSource);

app.MapGet("/", async () =>
{
    await ExecuteNonQuery(
        dataSource,
        "insert_demo_request",
        """
        INSERT INTO demo_requests DEFAULT VALUES;
        """);

    var count = await ExecuteScalar<long>(
        dataSource,
        "count_demo_requests",
        """
        SELECT COUNT(*) FROM demo_requests;
        """);

    if (Random.Shared.Next(1, 12) == 1)
    {
        return Results.Problem("Simulated backend error for Grafana dashboard demo.");
    }

    return Results.Ok(new
    {
        message = "Hello from .NET backend",
        totalBackendRequestsSavedInDb = count
    });
});

app.MapGet("/orders", async () =>
{
    var rows = await ExecuteOrdersQuery(dataSource);

    return Results.Ok(rows);
});

app.MapGet("/slow-query", async () =>
{
    var result = await ExecuteScalar<long>(
        dataSource,
        "intentional_slow_query",
        """
        SELECT COUNT(*) FROM demo_orders, pg_sleep(0.6);
        """);

    return Results.Ok(new
    {
        message = "Intentional slow query executed",
        orderCount = result
    });
});

app.MapGet("/db-health", async () =>
{
    try
    {
        await ExecuteScalar<int>(
            dataSource,
            "db_health_check",
            "SELECT 1;");

        dbHealthStatus.Set(1);

        return Results.Ok(new
        {
            database = "healthy"
        });
    }
    catch
    {
        dbHealthStatus.Set(0);

        return Results.Problem("Database is unhealthy.");
    }
});

app.MapGet("/health", () =>
{
    return Results.Ok(new
    {
        backend = "healthy"
    });
});

app.MapMetrics("/metrics");

app.Run();

async Task InitializeDatabase(NpgsqlDataSource source)
{
    await using var connection = await OpenObservedConnection(source);

    await using (var cmd = connection.CreateCommand())
    {
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS demo_requests (
                id SERIAL PRIMARY KEY,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS demo_orders (
                id SERIAL PRIMARY KEY,
                customer_name TEXT NOT NULL,
                amount NUMERIC NOT NULL,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );
            """;

        await cmd.ExecuteNonQueryAsync();
    }

    await using (var countCmd = connection.CreateCommand())
    {
        countCmd.CommandText = "SELECT COUNT(*) FROM demo_orders;";
        var count = Convert.ToInt64(await countCmd.ExecuteScalarAsync());

        if (count == 0)
        {
            for (var i = 1; i <= 100; i++)
            {
                await using var insertCmd = connection.CreateCommand();

                insertCmd.CommandText =
                    """
                    INSERT INTO demo_orders (customer_name, amount)
                    VALUES (@customer_name, @amount);
                    """;

                insertCmd.Parameters.AddWithValue("customer_name", $"customer-{i}");
                insertCmd.Parameters.AddWithValue("amount", Random.Shared.Next(10, 500));

                await insertCmd.ExecuteNonQueryAsync();
            }
        }
    }
}

async Task<NpgsqlConnection> OpenObservedConnection(NpgsqlDataSource source)
{
    var connection = await source.OpenConnectionAsync();
    dbActiveConnections.Inc();

    connection.StateChange += (_, args) =>
    {
        if (args.CurrentState == System.Data.ConnectionState.Closed)
        {
            dbActiveConnections.Dec();
        }
    };

    return connection;
}

async Task ExecuteNonQuery(
    NpgsqlDataSource source,
    string queryName,
    string sql)
{
    var stopwatch = Stopwatch.StartNew();

    try
    {
        await using var connection = await OpenObservedConnection(source);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText = sql;

        await cmd.ExecuteNonQueryAsync();

        ObserveDbSuccess(queryName, stopwatch.Elapsed);
    }
    catch
    {
        ObserveDbError(queryName, stopwatch.Elapsed);
        throw;
    }
}

async Task<T> ExecuteScalar<T>(
    NpgsqlDataSource source,
    string queryName,
    string sql)
{
    var stopwatch = Stopwatch.StartNew();

    try
    {
        await using var connection = await OpenObservedConnection(source);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText = sql;

        var result = await cmd.ExecuteScalarAsync();

        ObserveDbSuccess(queryName, stopwatch.Elapsed);

        return (T)Convert.ChangeType(result!, typeof(T));
    }
    catch
    {
        ObserveDbError(queryName, stopwatch.Elapsed);
        throw;
    }
}

async Task<List<object>> ExecuteOrdersQuery(NpgsqlDataSource source)
{
    const string queryName = "select_recent_orders";

    var stopwatch = Stopwatch.StartNew();

    try
    {
        await using var connection = await OpenObservedConnection(source);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText =
            """
            SELECT id, customer_name, amount, created_at
            FROM demo_orders
            ORDER BY created_at DESC
            LIMIT 10;
            """;

        await using var reader = await cmd.ExecuteReaderAsync();

        var rows = new List<object>();

        while (await reader.ReadAsync())
        {
            rows.Add(new
            {
                id = reader.GetInt32(0),
                customerName = reader.GetString(1),
                amount = reader.GetDecimal(2),
                createdAt = reader.GetDateTime(3)
            });
        }

        ObserveDbSuccess(queryName, stopwatch.Elapsed);

        return rows;
    }
    catch
    {
        ObserveDbError(queryName, stopwatch.Elapsed);
        throw;
    }
}

void ObserveDbSuccess(string queryName, TimeSpan duration)
{
    dbQueriesTotal.WithLabels(queryName, "success").Inc();
    dbQueryDuration.WithLabels(queryName).Observe(duration.TotalSeconds);

    if (duration.TotalMilliseconds >= slowQueryThresholdMs)
    {
        dbSlowQueriesTotal.WithLabels(queryName).Inc();
    }

    dbHealthStatus.Set(1);
}

void ObserveDbError(string queryName, TimeSpan duration)
{
    dbQueriesTotal.WithLabels(queryName, "error").Inc();
    dbQueryErrorsTotal.WithLabels(queryName).Inc();
    dbQueryDuration.WithLabels(queryName).Observe(duration.TotalSeconds);
    dbHealthStatus.Set(0);
}

int ExtractMaxPoolSize(string cs, int fallback)
{
    try
    {
        var builder = new NpgsqlConnectionStringBuilder(cs);
        return builder.MaxPoolSize;
    }
    catch
    {
        return fallback;
    }
}