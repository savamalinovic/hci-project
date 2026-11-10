using System.Collections.Generic;

namespace PaperlessDesktop.Models;

public sealed record PaperlessDocumentMetadata(
    IReadOnlyDictionary<int, string> Tags,
    IReadOnlyDictionary<int, string> DocumentTypes);
