using System.Text.Json.Serialization;

namespace PaperlessDesktop.Models;

public sealed class PaperlessTag
{
    [JsonPropertyName("id")]
    public required int Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("color")]
    public string? Color { get; init; }
}
