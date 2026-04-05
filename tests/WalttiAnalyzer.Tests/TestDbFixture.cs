using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using WalttiAnalyzer.Functions.Services;

namespace WalttiAnalyzer.Tests;

/// <summary>Shared helpers for tests that need a seeded SQLite database.</summary>
public class TestDbFixture : IDisposable
{
    public string DbPath { get; }
    public DatabaseService Db { get; }

    public TestDbFixture()
    {
        DbPath = Path.Combine(Path.GetTempPath(), $"waltti_test_{Guid.NewGuid()}.db");
        Db = new DatabaseService(NullLogger<DatabaseService>.Instance);
        Db.InitDb(DbPath);

        // Seed default test stop
        using var conn = Db.Connect(DbPath);
        Db.UpsertStop(conn, "Vaasa:309392", "Gerbynmäentie", null, 63.14, 21.57);
    }

    public SqliteConnection Connect() => Db.Connect(DbPath);

    public void Dispose()
    {
        try { File.Delete(DbPath); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }
}
