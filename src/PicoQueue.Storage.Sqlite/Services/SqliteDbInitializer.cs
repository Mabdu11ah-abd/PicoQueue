using PicoQueue.Storage.Sqlite.Connection;
using PicoQueue.Storage.Sqlite.Schema;

namespace PicoQueue.Storage.Sqlite.Services;

/// <summary>
/// Initializes SQLite database schema, tables, pragmas, and indexes idempotently.
/// </summary>
public class SqliteDbInitializer
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private int _isInitialized = 0;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public SqliteDbInitializer(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _isInitialized) == 1)
        {
            return;
        }

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _isInitialized) == 1)
            {
                return;
            }

            await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await SchemaDefinitions.EnsureInitializedAsync(connection, cancellationToken).ConfigureAwait(false);

            Volatile.Write(ref _isInitialized, 1);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
