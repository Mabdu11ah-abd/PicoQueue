using Microsoft.Data.Sqlite;

namespace PicoQueue.Storage.Sqlite.Connection;

/// <summary>
/// Factory abstraction for creating and opening SQLite connections.
/// </summary>
public interface ISqliteConnectionFactory
{
    /// <summary>
    /// Gets the connection string in use.
    /// </summary>
    string ConnectionString { get; }

    /// <summary>
    /// Creates a new un-opened <see cref="SqliteConnection"/>.
    /// </summary>
    SqliteConnection CreateConnection();

    /// <summary>
    /// Creates and asynchronously opens a new <see cref="SqliteConnection"/>.
    /// </summary>
    Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}
