using Microsoft.Data.Sqlite;
using PicoQueue.Storage.Sqlite.Schema;

namespace PicoQueue.Tests.Storage;

public class SchemaDefinitionsTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public SchemaDefinitionsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"picoqueue_test_schema_{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            var wal = _dbPath + "-wal";
            if (File.Exists(wal)) File.Delete(wal);
            var shm = _dbPath + "-shm";
            if (File.Exists(shm)) File.Delete(shm);
        }
        catch
        {
            // Ignore cleanup exceptions
        }
    }

    [Fact]
    public async Task EnsureInitializedAsync_ShouldCreateTablesAndIndexes()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        // Act
        await SchemaDefinitions.EnsureInitializedAsync(connection);

        // Assert tables exist in sqlite_master
        var tableNames = new List<string>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tableNames.Add(reader.GetString(0));
            }
        }

        Assert.Contains(SchemaDefinitions.Tables.QueueJobs, tableNames);
        Assert.Contains(SchemaDefinitions.Tables.ScheduledJobs, tableNames);

        // Assert indexes exist
        var indexNames = new List<string>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' ORDER BY name;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                indexNames.Add(reader.GetString(0));
            }
        }

        Assert.Contains("idx_queue_jobs_poll", indexNames);
        Assert.Contains("idx_queue_jobs_heartbeat", indexNames);
        Assert.Contains("idx_scheduled_jobs_due", indexNames);
    }

    [Fact]
    public async Task EnsureSchemaInitializedAsync_ExtensionMethod_ShouldBeIdempotent()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        // Calling multiple times should not throw
        await connection.EnsureSchemaInitializedAsync();
        await connection.EnsureSchemaInitializedAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='QueueJobs';";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }

    [Fact]
    public void EnsureInitialized_Synchronous_ShouldSucceed()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        SchemaDefinitions.EnsureInitialized(connection);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ScheduledJobs';";
        var count = Convert.ToInt32(cmd.ExecuteScalar());
        Assert.Equal(1, count);
    }
}
