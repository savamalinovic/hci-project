using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Input;
using PaperlessDesktop.Models;
using PaperlessDesktop.Services;

namespace PaperlessDesktop.ViewModels;

internal sealed class NaturalSearchViewModel : ObservableViewModel, IDisposable
{
    private readonly PaperlessDocumentMetadata? metadata;
    private readonly DocumentsViewModel documents;
    private readonly GeminiSearchInterpreter? gemini;
    private readonly CancellationTokenSource lifetime = new();
    private readonly AsyncCommand interpretCommand;
    private string query = string.Empty;
    private bool isBusy;
    private bool useGemini;
    private bool includeDocumentTitles;
    private IReadOnlyList<ProposedDocument> proposedDocuments = [];
    private bool hasInterpretation;
    private string status = string.Empty;
    private string text = string.Empty;
    private string from = string.Empty;
    private string to = string.Empty;
    private FilterOption? tag;
    private FilterOption? correspondent;
    private FilterOption? documentType;

    public NaturalSearchViewModel(DocumentsViewModel documents, GeminiSearchInterpreter? gemini)
    {
        this.documents = documents;
        metadata = documents.SearchMetadata;
        this.gemini = gemini;
        useGemini = gemini is not null;
        Tags = documents.Tags;
        Correspondents = documents.Correspondents;
        DocumentTypes = documents.DocumentTypes;
        tag = Tags[0];
        correspondent = Correspondents[0];
        documentType = DocumentTypes[0];
        interpretCommand = new AsyncCommand(() => CanEditQuery && !string.IsNullOrWhiteSpace(Query), InterpretAsync);
    }

    public IReadOnlyList<FilterOption> Tags { get; }
    public IReadOnlyList<FilterOption> Correspondents { get; }
    public IReadOnlyList<FilterOption> DocumentTypes { get; }
    public ICommand InterpretCommand => interpretCommand;
    public bool IsBusy => isBusy;
    public bool CanEditQuery => !IsBusy;
    public bool HasInterpretation => hasInterpretation;
    public string Status => status;
    public IReadOnlyList<ProposedDocument> ProposedDocuments => proposedDocuments;
    public bool HasProposedDocuments => proposedDocuments.Count > 0;

    public bool IncludeDocumentTitles
    {
        get => includeDocumentTitles;
        set
        {
            if (SetProperty(ref includeDocumentTitles, value))
            {
                ResetInterpretation();
            }
        }
    }

    public bool UseGemini
    {
        get => useGemini;
        set
        {
            if (SetProperty(ref useGemini, value))
            {
                NotifyPropertyChanged(nameof(UseLocalParser));
                ResetInterpretation();
                if (value && gemini is null)
                {
                    SetStatus("NaturalGeminiMissingKey");
                }
            }
        }
    }

    public bool UseLocalParser
    {
        get => !UseGemini;
        set
        {
            if (value)
            {
                UseGemini = false;
            }
        }
    }

    public string Query
    {
        get => query;
        set
        {
            if (SetProperty(ref query, value))
            {
                ResetInterpretation();
                interpretCommand.Refresh();
            }
        }
    }

    public string Text
    {
        get => text;
        set => SetProperty(ref text, value);
    }

    public string From
    {
        get => from;
        set => SetProperty(ref from, value);
    }

    public string To
    {
        get => to;
        set => SetProperty(ref to, value);
    }

    public FilterOption? Tag
    {
        get => tag;
        set => SetProperty(ref tag, value);
    }

    public FilterOption? Correspondent
    {
        get => correspondent;
        set => SetProperty(ref correspondent, value);
    }

    public FilterOption? DocumentType
    {
        get => documentType;
        set => SetProperty(ref documentType, value);
    }

    public bool TryGetCriteria(out DocumentSearchCriteria criteria)
    {
        criteria = new DocumentSearchCriteria();
        if (!HasInterpretation || IsBusy)
        {
            return false;
        }

        if (!TryReadDate(From, out var first) || !TryReadDate(To, out var last))
        {
            SetStatus("NaturalInvalidDate");
            return false;
        }

        if (first > last)
        {
            SetStatus("NaturalReversedDates");
            return false;
        }

        var ids = ProposedDocuments.Where(document => document.IsSelected).Select(document => document.Id).ToArray();
        if (HasProposedDocuments && ids.Length == 0)
        {
            SetStatus("NaturalSelectDocument");
            return false;
        }

        criteria = new DocumentSearchCriteria(string.IsNullOrWhiteSpace(Text) ? null : Text.Trim(),
            Tag?.Id, Correspondent?.Id, DocumentType?.Id, first, last, ids.Length > 0 ? ids : null);
        if (criteria == new DocumentSearchCriteria())
        {
            SetStatus("NaturalEmptyCriteria");
            return false;
        }

        return true;
    }

    private async Task InterpretAsync()
    {
        if (UseGemini && gemini is null)
        {
            SetStatus("NaturalGeminiMissingKey");
            return;
        }

        var request = new NaturalSearchRequest(Query.Trim(), DateOnly.FromDateTime(DateTime.Today), metadata);
        var cancellationToken = lifetime.Token;
        SetBusy(true);
        SetProperty(ref hasInterpretation, false, nameof(HasInterpretation));
        SetStatus("NaturalInterpreting");

        try
        {
            var titles = UseGemini && IncludeDocumentTitles
                ? await documents.GetDocumentTitlesAsync(cancellationToken)
                : null;
            request = request with { DocumentTitles = titles };
            var result = UseGemini
                ? await gemini!.InterpretAsync(request, cancellationToken)
                : LocalSearchInterpreter.Interpret(request);
            cancellationToken.ThrowIfCancellationRequested();
            var criteria = result.Criteria;
            Text = criteria.Text ?? string.Empty;
            Tag = Tags.FirstOrDefault(option => option.Id == criteria.TagId) ?? Tags[0];
            Correspondent = Correspondents.FirstOrDefault(option => option.Id == criteria.CorrespondentId) ?? Correspondents[0];
            DocumentType = DocumentTypes.FirstOrDefault(option => option.Id == criteria.DocumentTypeId) ?? DocumentTypes[0];
            From = FormatDate(criteria.CreatedFrom);
            To = FormatDate(criteria.CreatedTo);
            proposedDocuments = criteria.DocumentIds?.Select(id => new ProposedDocument(id, titles![id])).ToArray() ?? [];
            NotifyPropertyChanged(nameof(ProposedDocuments));
            NotifyPropertyChanged(nameof(HasProposedDocuments));
            var sourceMessage = LocalizationService.Text(UseGemini ? "NaturalGeminiResult" : "NaturalLocalResult");
            SetProperty(ref status, result.NeedsClarification
                ? LocalizationService.Format("NaturalNeedsReview", sourceMessage)
                : sourceMessage, nameof(Status));
            SetProperty(ref hasInterpretation, true, nameof(HasInterpretation));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (UseGemini && (error is HttpRequestException or JsonException or InvalidDataException
                                           or FormatException or ArgumentException or OperationCanceledException))
        {
            SetStatus("NaturalGeminiFailed");
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SetBusy(false);
            }
        }
    }

    private void ResetInterpretation()
    {
        SetProperty(ref hasInterpretation, false, nameof(HasInterpretation));
        SetProperty(ref status, string.Empty, nameof(Status));
    }

    private void SetBusy(bool value)
    {
        if (SetProperty(ref isBusy, value, nameof(IsBusy)))
        {
            NotifyPropertyChanged(nameof(CanEditQuery));
            interpretCommand.Refresh();
        }
    }

    private void SetStatus(string key) => SetProperty(ref status, LocalizationService.Text(key), nameof(Status));

    private static string FormatDate(DateOnly? date) =>
        date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    private static bool TryReadDate(string input, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return true;
        }

        if (!DateOnly.TryParseExact(input.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        date = parsed;
        return true;
    }

    public void Dispose()
    {
        lifetime.Cancel();
        lifetime.Dispose();
    }
}

internal sealed class ProposedDocument(int id, string title)
{
    public int Id { get; } = id;
    public string Title { get; } = title;
    public bool IsSelected { get; set; } = true;
}
