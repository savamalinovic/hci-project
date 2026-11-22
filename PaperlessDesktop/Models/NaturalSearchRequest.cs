namespace PaperlessDesktop.Models;

internal sealed record NaturalSearchRequest(string Query, DateOnly Today, PaperlessDocumentMetadata? Metadata);

internal sealed record NaturalSearchInterpretation(DocumentSearchCriteria Criteria, bool NeedsClarification = false);
