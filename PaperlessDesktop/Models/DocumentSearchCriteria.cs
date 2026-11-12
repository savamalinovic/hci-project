namespace PaperlessDesktop.Models;

public sealed record DocumentSearchCriteria(
    string? Text = null,
    int? TagId = null,
    int? CorrespondentId = null,
    int? DocumentTypeId = null);
