namespace PicoQueue.Storage.Sqlite.Connection;

/// <summary>
/// Options for configuring the SQLite storage provider.
/// </summary>
public class SqliteStorageOptions
{
    /// <summary>
    /// Gets or sets the SQLite connection string. Default is "Data Source=picoqueue.db;Cache=Default".
    /// </summary>
    public string ConnectionString { get; set; } = "Data Source=picoqueue.db;Cache=Default";

    /// <summary>
    /// Gets or sets the busy timeout in milliseconds to wait before SQLite throws a lock exception. Default is 5000ms.
    /// </summary>
    public int BusyTimeoutMilliseconds { get; set; } = 5000;

    /// <summary>
    /// Gets or sets whether to automatically execute schema creation (CREATE TABLE IF NOT EXISTS) on startup. Default is true.
    /// </summary>
    public bool AutoInitializeSchema { get; set; } = true;
}
