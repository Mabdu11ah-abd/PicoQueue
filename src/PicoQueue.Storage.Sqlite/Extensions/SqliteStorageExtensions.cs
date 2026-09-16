using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PicoQueue.Abstractions.Interfaces;
using PicoQueue.Storage.Sqlite.Connection;
using PicoQueue.Storage.Sqlite.Services;

namespace PicoQueue.Storage.Sqlite.Extensions;

/// <summary>
/// Extension methods for registering SQLite storage with the dependency injection container.
/// </summary>
public static class SqliteStorageExtensions
{
    /// <summary>
    /// Adds PicoQueue SQLite storage services to the specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configure">Optional delegate to configure <see cref="SqliteStorageOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddPicoQueueSqlite(
        this IServiceCollection services,
        Action<SqliteStorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new SqliteStorageOptions();
        configure?.Invoke(options);

        services.Configure<SqliteStorageOptions>(opt =>
        {
            opt.ConnectionString = options.ConnectionString;
            opt.BusyTimeoutMilliseconds = options.BusyTimeoutMilliseconds;
            opt.AutoInitializeSchema = options.AutoInitializeSchema;
        });

        services.TryAddSingleton<ISqliteConnectionFactory, SqliteConnectionFactory>();
        services.TryAddSingleton<SqliteDbInitializer>();
        services.TryAddSingleton<IJobQueue, SqliteJobQueue>();
        services.TryAddSingleton<IScheduleJob, SqliteScheduleJob>();

        return services;
    }

    /// <summary>
    /// Adds PicoQueue SQLite storage services using a custom connection string.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="connectionString">The SQLite connection string to use.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddPicoQueueSqlite(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));
        }

        return services.AddPicoQueueSqlite(options =>
        {
            options.ConnectionString = connectionString;
        });
    }
}
