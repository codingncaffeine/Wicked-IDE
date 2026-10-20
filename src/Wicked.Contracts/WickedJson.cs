using System.Text.Json.Serialization;

namespace Wicked.Contracts;

/// <summary>
/// The JSON every Wicked process speaks to every other: camelCase names, enums as strings, generated at build time so no
/// reflection runs when a message is read or written.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HealthReport))]
public sealed partial class WickedJson : JsonSerializerContext;
