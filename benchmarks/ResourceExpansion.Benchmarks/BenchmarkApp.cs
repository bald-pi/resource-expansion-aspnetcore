using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ResourceExpansion.Api.Domain;
using ResourceExpansion.Api.Infrastructure;

namespace ResourceExpansion.Benchmarks;

// The Docker environment from benchmarks/docker/compose.yaml: the API in its own container,
// PostgreSQL in another, and Toxiproxy adding latency to both links. Started once and shared
// by every benchmark case.
public sealed class BenchmarkApp : IAsyncDisposable
{
    public const int MembershipId = 1;
    public const int VisitCount = 1_000;
    public const int ClubCount = 20;

    // Added to every API-to-database round trip: a database on another host in the same data center.
    public const int DatabaseLatencyMs = 1;

    private const string ApiUrl = "http://localhost:18080";
    private const string ToxiproxyUrl = "http://localhost:8474";
    private const string DatabaseConnectionString =
        "Host=localhost;Port=54330;Database=expansion_bench;Username=bench;Password=bench";

    private static Task<BenchmarkApp>? instance;

    private readonly HttpClient toxiproxy = new() { BaseAddress = new Uri(ToxiproxyUrl) };
    private readonly NpgsqlDataSource database = NpgsqlDataSource.Create(DatabaseConnectionString);

    private BenchmarkApp() { }

    public HttpClient Client { get; } = new() { BaseAddress = new Uri(ApiUrl) };

    public static Task<BenchmarkApp> InstanceAsync() => instance ??= StartAsync();

    public static async Task DisposeInstanceAsync()
    {
        if (instance is { IsCompletedSuccessfully: true }) await instance.Result.DisposeAsync();
    }

    private static async Task<BenchmarkApp> StartAsync()
    {
        var app = new BenchmarkApp();
        try
        {
            await ComposeAsync("up", "-d", "--build", "--wait");
            await app.WaitForApiAsync();
            await app.SeedAsync();
            await app.SetLatencyAsync("postgres", DatabaseLatencyMs);
            await app.WarmUpAsync();
            return app;
        }
        catch
        {
            // Do not leave the containers running when the environment fails to start.
            await app.DisposeAsync();
            throw;
        }
    }

    // Latency added to every response from the API: the client's network round trip.
    public Task SetNetworkLatencyAsync(int milliseconds) => SetLatencyAsync("api", milliseconds);

    // pg_stat_statements counts statements as PostgreSQL executes them, whichever client sent them.
    // Only SELECTs count: Npgsql also sends DISCARD ALL when it reuses a pooled connection.
    public async Task ResetQueryCountAsync()
    {
        await using var command = database.CreateCommand("SELECT pg_stat_statements_reset()");
        await command.ExecuteNonQueryAsync();
    }

    public async Task<int> QueryCountAsync()
    {
        await using var command = database.CreateCommand("""
            SELECT coalesce(sum(calls), 0)::int
            FROM pg_stat_statements
            WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
              AND query ILIKE 'SELECT%' AND query NOT ILIKE '%pg_stat_statements%'
            """);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task SetLatencyAsync(string proxy, int milliseconds)
    {
        // Returns 404 when there is no toxic yet.
        using (await toxiproxy.DeleteAsync($"/proxies/{proxy}/toxics/latency")) { }
        if (milliseconds == 0) return;

        using var response = await toxiproxy.PostAsJsonAsync($"/proxies/{proxy}/toxics", new
        {
            name = "latency", type = "latency", stream = "downstream",
            attributes = new { latency = milliseconds, jitter = 0 }
        });
        response.EnsureSuccessStatusCode();
    }

    // The API creates the schema and its demo data before it starts listening, so any HTTP answer means
    // it is ready. 404 is ready too: a database left by an interrupted run holds the benchmark data instead.
    private async Task WaitForApiAsync()
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using var response = await Client.GetAsync("/api/memberships/1001");
                if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound) return;
            }
            catch (HttpRequestException) { }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new TimeoutException("The API container did not become ready.");
    }

    // Replaces the demo data with one membership whose visits are spread round-robin over the clubs,
    // so a preview of N visits references min(N, ClubCount) distinct clubs.
    private async Task SeedAsync()
    {
        var options = new DbContextOptionsBuilder<MembershipsDbContext>().UseNpgsql(DatabaseConnectionString).Options;
        await using var db = new MembershipsDbContext(options);
        await db.Database.ExecuteSqlRawAsync("""TRUNCATE "Visit", "Memberships", "Member", "Club" CASCADE""");

        var clubs = Enumerable.Range(1, ClubCount)
            .Select(id => new Club { Id = id, Name = $"Club {id}", City = "Seattle" })
            .ToList();
        var firstVisit = new DateTimeOffset(2026, 1, 1, 6, 0, 0, TimeSpan.Zero);
        db.Memberships.Add(new Membership
        {
            Id = MembershipId, Number = "MEM-0001", Plan = "Premium", Status = "active",
            StartsOn = new(2026, 1, 1), EndsOn = new(2026, 12, 31), MonthlyFeeCents = 4900,
            Member = new Member { Id = 1, Name = "Alice Morgan", Email = "alice@example.com" },
            Visits = Enumerable.Range(1, VisitCount)
                .Select(id => new Visit { Id = id, CheckedInAt = firstVisit.AddHours(id * 6), Club = clubs[id % ClubCount] })
                .ToList()
        });
        await db.SaveChangesAsync();
        // Fresh statistics now, so autovacuum does not vacuum and analyze the new rows during the first cases.
        await db.Database.ExecuteSqlRawAsync("VACUUM ANALYZE");
        await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS pg_stat_statements");
    }

    // Runs every client until the API has tiered up its JIT code, EF Core has compiled its queries, and
    // both connection pools are filled. Without it the first case measured the environment warming up.
    private async Task WarmUpAsync()
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(20))
        {
            await Scenarios.ExpandedAsync(Client, ClubCount);
            await Scenarios.SeparateParallelAsync(Client, ClubCount);
            await Scenarios.SeparateSequentialAsync(Client, ClubCount);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        toxiproxy.Dispose();
        await database.DisposeAsync();
        await ComposeAsync("down", "-v");
    }

    private static async Task ComposeAsync(params string[] args)
    {
        var composeFile = Path.Combine(RepositoryRoot(), "benchmarks", "docker", "compose.yaml");
        var startInfo = new ProcessStartInfo("docker");
        foreach (var arg in (string[])["compose", "-f", composeFile, "-p", "resource-expansion-bench", .. args])
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"docker compose {string.Join(' ', args)} failed with exit code {process.ExitCode}.");
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ResourceExpansion.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not find the repository root (ResourceExpansion.slnx).");
    }
}
