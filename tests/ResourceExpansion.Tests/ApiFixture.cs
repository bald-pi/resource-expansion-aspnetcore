using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ResourceExpansion.Api.Infrastructure;
using Testcontainers.PostgreSql;

namespace ResourceExpansion.Tests;

public sealed class ApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    public SqlRecorder Sql { get; } = new();
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        Factory = new TestApplication(postgres.GetConnectionString(), Sql);
        // Force startup and seeding before recording request queries.
        using var client = Factory.CreateClient();
        Sql.Commands.Clear();
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null) await Factory.DisposeAsync();
        await postgres.DisposeAsync();
    }

    private sealed class TestApplication(string connectionString, SqlRecorder recorder) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Orders"] = connectionString }));
            builder.ConfigureServices(services =>
                services.AddDbContext<OrdersDbContext>(options => options.AddInterceptors(recorder)));
        }
    }
}

public sealed class SqlRecorder : DbCommandInterceptor
{
    public ConcurrentQueue<string> Commands { get; } = new();

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Commands.Enqueue(command.CommandText);
        return ValueTask.FromResult(result);
    }
}
