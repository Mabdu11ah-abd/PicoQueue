namespace PicoQueue.Abstractions.Serialization;

/// <summary>
/// Abstraction for serializing and deserializing job payloads.
/// </summary>
public interface IJobSerializer
{
    /// <summary>
    /// Serializes a strongly-typed argument object into a string (e.g. JSON).
    /// </summary>
    string Serialize<T>(T value);

    /// <summary>
    /// Serializes an object with explicit type info into a string (e.g. JSON).
    /// </summary>
    string Serialize(object value, Type type);

    /// <summary>
    /// Deserializes a string payload back into a strongly-typed argument object.
    /// </summary>
    T? Deserialize<T>(string payload);

    /// <summary>
    /// Deserializes a string payload into the specified target type.
    /// </summary>
    object? Deserialize(string payload, Type type);
}
