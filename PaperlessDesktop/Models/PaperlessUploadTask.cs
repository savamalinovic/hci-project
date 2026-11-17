namespace PaperlessDesktop.Models;

public sealed record PaperlessUploadTask(string Status, int? DocumentId, string? Result);
