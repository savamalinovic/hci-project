using System;
using System.Collections.Generic;

namespace PaperlessDesktop.Models;

public sealed record DocumentUploadRequest(
    string FilePath,
    string? Title = null,
    DateOnly? Created = null,
    int? CorrespondentId = null,
    int? DocumentTypeId = null,
    IReadOnlyList<int>? TagIds = null);
