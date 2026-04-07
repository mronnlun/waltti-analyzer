using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WalttiAnalyzer.Core.Data;
using WalttiAnalyzer.Core.Services;

namespace WalttiAnalyzer.Tests;

/// <summary>Shared helpers for tests that need a seeded in-memory SQLite database.</summary>
public class TestDbFixture : IDisposable
{
    private readonly SqliteConnection _keepAliveConnection;
    public WalttiDbContext Context { get; }
    public DatabaseService Db { get; }

    public TestDbFixture()
    {
        // Keep a connection open so the in-memory SQLite DB persists across operations.
        _keepAliveConnection = new SqliteConnection("Data Source=:memory:");
        _keepAliveConnection.Open();

        var options = new DbContextOptionsBuilder<WalttiDbContext>()
            .UseSqlite(_keepAliveConnection)
            .Options;

        Context = new WalttiDbContext(options);
        Context.Database.EnsureCreated();

        Db = new DatabaseService(Context, NullLogger<DatabaseService>.Instance);

        // Seed default test stop
        Db.UpsertStopAsync("Vaasa:309392", "Gerbynmäentie", null, 63.14, 21.57).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Context.Dispose();
        _keepAliveConnection.Dispose();
        GC.SuppressFinalize(this);
    }
}
