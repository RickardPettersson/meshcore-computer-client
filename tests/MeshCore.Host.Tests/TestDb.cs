using MeshCore.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MeshCore.Host.Tests;

/// <summary>An in-memory SQLite database with the real migrations applied. Lives as long as the instance.</summary>
internal sealed class TestDb : IDbContextFactory<MeshDbContext>, IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<MeshDbContext> _options;

    private TestDb()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<MeshDbContext>().UseSqlite(_connection).Options;
    }

    public static async Task<TestDb> CreateAsync()
    {
        var db = new TestDb();
        await using var ctx = db.CreateDbContext();
        await ctx.Database.MigrateAsync();
        return db;
    }

    public MeshDbContext CreateDbContext() => new(_options);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
