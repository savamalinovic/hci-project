using System;
using System.Collections.Generic;

namespace PaperlessDesktop.Models;

public sealed record DocumentSearchCriteria(
    string? Text = null,
    int? TagId = null,
    int? CorrespondentId = null,
    int? DocumentTypeId = null,
    DateOnly? CreatedFrom = null,
    DateOnly? CreatedTo = null,
    IReadOnlyList<int>? DocumentIds = null);
