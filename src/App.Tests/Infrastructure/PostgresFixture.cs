using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(FamilyMemories.Tests.Infrastructure.PostgresFixture))]

namespace FamilyMemories.Tests.Infrastructure;

/// <summary>One PostgreSQL 17 container for the whole test run; each test class gets its own database.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Image = "postgres:17.12-trixie";

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder(Image)
        .WithCommand("-c", "max_connections=500")
        .Build();

    public async ValueTask InitializeAsync() => await container.StartAsync();

    public async ValueTask DisposeAsync() => await container.DisposeAsync();

    public async Task<string> CreateDatabaseAsync()
    {
        var name = "test_" + Guid.NewGuid().ToString("N")[..12];
        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
        await cmd.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(container.GetConnectionString()) 
        {
            Database = name,
            // Many test classes run in parallel, each with its own database and pool.
            MaxPoolSize = 20,
            ConnectionIdleLifetime = 15,
        }.ConnectionString;
    }
}
