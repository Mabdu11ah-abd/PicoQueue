using System.Collections.Concurrent;
using PicoQueue.Abstractions.DTOs.Queue;
using PicoQueue.Storage.Sqlite.Connection;
using PicoQueue.Storage.Sqlite.Services;

namespace PicoQueue.Tests.Storage;

public class SqliteConcurrencyTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly SqliteDbInitializer _initializer;

    public SqliteConcurrencyTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"picoqueue_test_concurrency_{Guid.NewGuid():N}.db");
        // Use WAL mode and busy timeout = 10000ms for stress test
        _connectionString = $"Data Source={_dbPath};Cache=Default";
        _connectionFactory = new SqliteConnectionFactory(new SqliteStorageOptions
        {
            ConnectionString = _connectionString,
            BusyTimeoutMilliseconds = 10000
        });
        _initializer = new SqliteDbInitializer(_connectionFactory);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
            // Ignore temp file cleanup exceptions
        }
    }

    [Fact]
    public async Task ConcurrentWorkers_ShouldNeverClaimSameJobTwice()
    {
        await _initializer.InitializeAsync();
        var mainQueue = new SqliteJobQueue(_connectionFactory, _initializer);

        const int totalJobs = 50;
        const int workerCount = 8;

        // 1. Enqueue 50 jobs
        var enqueuedIds = new HashSet<Guid>();
        for (int i = 0; i < totalJobs; i++)
        {
            var job = await mainQueue.EnqueueAsync(new EnqueueJobDto
            {
                TypeName = $"StressJob_{i}",
                PayloadJson = $"{{\"index\":{i}}}",
                Priority = i % 5,
                Queue = "stress-test"
            });
            enqueuedIds.Add(job.Id);
        }

        Assert.Equal(totalJobs, enqueuedIds.Count);

        // 2. Spin up concurrent worker loops
        var claimedIds = new ConcurrentBag<Guid>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var workerTasks = Enumerable.Range(0, workerCount).Select(async workerIndex =>
        {
            var workerQueue = new SqliteJobQueue(_connectionFactory, _initializer);
            var workerId = $"worker-{workerIndex}";

            while (!cts.Token.IsCancellationRequested)
            {
                if (claimedIds.Count >= totalJobs)
                {
                    break;
                }

                var job = await workerQueue.DequeueAsync(new DequeueJobDto
                {
                    WorkerId = workerId,
                    Queue = "stress-test"
                }, cts.Token);

                if (job == null)
                {
                    // No job currently available; brief delay or break if all jobs done
                    if (claimedIds.Count >= totalJobs)
                    {
                        break;
                    }
                    await Task.Delay(10, cts.Token);
                    continue;
                }

                claimedIds.Add(job.Id);

                // Simulate brief execution time
                await Task.Delay(Random.Shared.Next(2, 10), cts.Token);

                // Acknowledge completion
                await workerQueue.AcknowledgeSuccessAsync(job.Id, cts.Token);
            }
        });

        await Task.WhenAll(workerTasks);

        // 3. Verify exactly totalJobs were claimed, and no duplicates exist
        Assert.Equal(totalJobs, claimedIds.Count);
        var distinctClaimed = claimedIds.Distinct().ToList();
        Assert.Equal(totalJobs, distinctClaimed.Count);

        // All claimed IDs must match the enqueued IDs
        foreach (var id in distinctClaimed)
        {
            Assert.Contains(id, enqueuedIds);
        }
    }
}
