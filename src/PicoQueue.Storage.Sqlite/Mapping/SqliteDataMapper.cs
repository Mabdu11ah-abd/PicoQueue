using System.Globalization;
using Microsoft.Data.Sqlite;
using PicoQueue.Abstractions.Enums;
using PicoQueue.Abstractions.Models;

namespace PicoQueue.Storage.Sqlite.Mapping;

/// <summary>
/// High-performance mapper for converting <see cref="SqliteDataReader"/> rows to PicoQueue domain models.
/// </summary>
public static class SqliteDataMapper
{
    public static QueueJob MapQueueJob(SqliteDataReader reader)
    {
        return new QueueJob
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
            Name = reader.GetString(reader.GetOrdinal("Name")),
            Description = GetNullableString(reader, "Description"),
            TypeName = reader.GetString(reader.GetOrdinal("TypeName")),
            PayloadJson = reader.GetString(reader.GetOrdinal("PayloadJson")),
            Queue = reader.GetString(reader.GetOrdinal("Queue")),
            State = Enum.Parse<JobState>(reader.GetString(reader.GetOrdinal("State")), ignoreCase: true),
            Priority = reader.GetInt32(reader.GetOrdinal("Priority")),
            StartedAt = GetNullableDateTimeOffset(reader, "StartedAt"),
            CompletedAt = GetNullableDateTimeOffset(reader, "CompletedAt"),
            HeartbeatAt = GetNullableDateTimeOffset(reader, "HeartbeatAt"),
            Attempt = reader.GetInt32(reader.GetOrdinal("Attempt")),
            MaxAttempts = reader.GetInt32(reader.GetOrdinal("MaxAttempts")),
            RetryAt = GetNullableDateTimeOffset(reader, "RetryAt"),
            ErrorMessage = GetNullableString(reader, "ErrorMessage"),
            ErrorDetail = GetNullableString(reader, "ErrorDetail"),
            WorkerId = GetNullableString(reader, "WorkerId"),
            ScheduledJobId = GetNullableGuid(reader, "ScheduledJobId"),
            CreatedAt = ParseDateTimeOffset(reader.GetString(reader.GetOrdinal("CreatedAt"))),
            LastUpdate = ParseDateTimeOffset(reader.GetString(reader.GetOrdinal("LastUpdate")))
        };
    }

    public static ScheduledJob MapScheduledJob(SqliteDataReader reader)
    {
        return new ScheduledJob
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
            Name = reader.GetString(reader.GetOrdinal("Name")),
            Description = GetNullableString(reader, "Description"),
            TypeName = reader.GetString(reader.GetOrdinal("TypeName")),
            PayloadJson = reader.GetString(reader.GetOrdinal("PayloadJson")),
            Queue = reader.GetString(reader.GetOrdinal("Queue")),
            ScheduledAt = ParseDateTimeOffset(reader.GetString(reader.GetOrdinal("ScheduledAt"))),
            IsRecurring = reader.GetInt32(reader.GetOrdinal("IsRecurring")) != 0,
            CronExpression = GetNullableString(reader, "CronExpression"),
            LastTriggeredAt = GetNullableDateTimeOffset(reader, "LastTriggeredAt"),
            NextRunAt = GetNullableDateTimeOffset(reader, "NextRunAt"),
            IsEnabled = reader.GetInt32(reader.GetOrdinal("IsEnabled")) != 0,
            CreatedAt = ParseDateTimeOffset(reader.GetString(reader.GetOrdinal("CreatedAt"))),
            LastUpdate = ParseDateTimeOffset(reader.GetString(reader.GetOrdinal("LastUpdate"))),
            UpdatedAt = GetNullableDateTimeOffset(reader, "UpdatedAt")
        };
    }

    public static string? GetNullableString(SqliteDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public static Guid? GetNullableGuid(SqliteDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        string val = reader.GetString(ordinal);
        return string.IsNullOrEmpty(val) ? null : Guid.Parse(val);
    }

    public static DateTimeOffset? GetNullableDateTimeOffset(SqliteDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        string val = reader.GetString(ordinal);
        return string.IsNullOrEmpty(val) ? null : ParseDateTimeOffset(val);
    }

    public static DateTimeOffset ParseDateTimeOffset(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
