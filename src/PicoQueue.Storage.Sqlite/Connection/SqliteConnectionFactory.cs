using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace PicoQueue.Storage.Sqlite.Connection;

/// <summary>
/// Default implementation of <see cref="ISqliteConnectionFactory"/> for managing SQLite connections.
/// </summary>
public class SqliteConnectionFactory : ISqliteConnectionFactory
{
    private readonly SqliteStorageOptions _options;

    public string ConnectionString => _options.ConnectionString;

    public SqliteConnectionFactory(IOptions<SqliteStorageOptions> options)
    {
        _options = options.Value ?? new SqliteStorageOptions();
    }

    public SqliteConnectionFactory(SqliteStorageOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public SqliteConnectionFactory(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));
        }

        _options = new SqliteStorageOptions { ConnectionString = connectionString };
    }

    public SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder(_options.ConnectionString);
        if (builder.DefaultTimeout == 0 && _options.BusyTimeoutMilliseconds > 0)
        {
            builder.DefaultTimeout = Math.Max(1, _options.BusyTimeoutMilliseconds / 1000);
        }

        return new SqliteConnection(builder.ToString());
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
