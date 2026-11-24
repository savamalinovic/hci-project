namespace PaperlessDesktop.Models;

internal sealed record NaturalSearchRequest(string Query, DateOnly Today, PaperlessDocumentMetadata? Metadata,
    IReadOnlyDictionary<int, string>? DocumentTitles = null);

internal sealed record NaturalSearchInterpretation(DocumentSearchCriteria Criteria, bool NeedsClarification = false);
