using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaperlessDesktop.Models;

public sealed class PaperlessDocument
{
    [JsonPropertyName("id")]
    public required int Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("created")]
    [JsonConverter(typeof(DocumentDateConverter))]
    public DateOnly? Created { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<int> TagIds { get; init; } = [];

    [JsonPropertyName("correspondent")]
    public int? CorrespondentId { get; init; }

    [JsonPropertyName("document_type")]
    public int? DocumentTypeId { get; init; }

    private sealed class DocumentDateConverter : JsonConverter<DateOnly?>
    {
        public override DateOnly? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out var date))
            {
                // Older API versions also return a timestamp in the created field.
                return DateOnly.FromDateTime(date.DateTime);
            }

            throw new JsonException("Paperless returned an invalid document date.");
        }

        public override void Write(
            Utf8JsonWriter writer,
            DateOnly? value,
            JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value, options);
    }
}

public sealed class PaperlessDocumentPage
{
    [JsonPropertyName("count")]
    public required int Count { get; init; }

    [JsonPropertyName("results")]
    public required IReadOnlyList<PaperlessDocument> Results { get; init; }
}
