using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using PaperlessDesktop.Models;
using PaperlessDesktop.Services;

namespace PaperlessDesktop.ViewModels;

internal sealed class DocumentsViewModel : ObservableViewModel, IDisposable
{
    private static readonly FilterOption AllTags = new(null, "AllTags", localized: true);
    private static readonly FilterOption AllCorrespondents = new(null, "AllCorrespondents", localized: true);
    private static readonly FilterOption AllTypes = new(null, "AllTypes", localized: true);
    private static readonly DocumentSearchCriteria EmptyCriteria = new();

    private readonly PaperlessClient client;
    private readonly CancellationTokenSource lifetime = new();
    private readonly AsyncCommand previousPageCommand;
    private readonly AsyncCommand nextPageCommand;
    private readonly AsyncCommand applyFiltersCommand;
    private readonly AsyncCommand clearFiltersCommand;
    private Uri? server;
    private string token = string.Empty;
    private PaperlessDocumentMetadata? metadata;
    private DocumentSearchCriteria activeCriteria = EmptyCriteria;
    private PaperlessDocumentPage? currentPage;
    private int pageNumber;
    private IReadOnlyList<DocumentListItem> documents = [];
    private DocumentListItem? selectedDocument;
    private string searchText = string.Empty;
    private FilterOption? selectedTag = AllTags;
    private FilterOption? selectedCorrespondent = AllCorrespondents;
    private FilterOption? selectedDocumentType = AllTypes;
    private bool isBusy;
    private LocalizedMessage statusMessage = new("DocumentsConnectPrompt");

    public DocumentsViewModel(PaperlessClient client)
    {
        this.client = client;
        Preview = new DocumentPreviewViewModel(client);
        previousPageCommand = new AsyncCommand(
            () => !IsBusy && currentPage?.HasPreviousPage == true,
            () => LoadPageAsync(pageNumber - 1, activeCriteria));
        nextPageCommand = new AsyncCommand(
            () => !IsBusy && currentPage?.HasNextPage == true,
            () => LoadPageAsync(pageNumber + 1, activeCriteria));
        applyFiltersCommand = new AsyncCommand(() => CanSearch, ApplyFiltersAsync);
        clearFiltersCommand = new AsyncCommand(() => CanSearch && HasSearchInput, ClearFiltersAsync);
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    public DocumentPreviewViewModel Preview { get; }
    internal Guid? PendingUploadTaskId { get; private set; }
    internal PaperlessDocumentMetadata? SearchMetadata => metadata;

    internal Task<IReadOnlyDictionary<int, string>> GetDocumentTitlesAsync(CancellationToken cancellationToken) =>
        client.GetDocumentTitlesAsync(server!, token, cancellationToken);

    internal (Uri Server, string Token)? GetActiveConnection() =>
        CanSearch && server is { } activeServer ? (activeServer, token) : null;

    public IReadOnlyList<DocumentListItem> Documents
    {
        get => documents;
        private set => SetProperty(ref documents, value);
    }

    public DocumentListItem? SelectedDocument
    {
        get => selectedDocument;
        set
        {
            if (SetProperty(ref selectedDocument, value))
            {
                Preview.Show(value, server, token);
            }
        }
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                NotifyPropertyChanged(nameof(CanConnect));
                NotifyPropertyChanged(nameof(CanSearch));
                RefreshCommands();
            }
        }
    }

    public bool CanConnect => !IsBusy;
    public bool CanSearch => !IsBusy && currentPage is not null;
    public bool IsConnectionRequired => currentPage is null;

    public string PageLabel => currentPage is null ? string.Empty : LocalizationService.Format("PageNumber", pageNumber);
    public string StatusMessage => statusMessage.ToString();
    public bool HasDateCriteria => activeCriteria.CreatedFrom is not null || activeCriteria.CreatedTo is not null;
    public bool HasDocumentSelection => activeCriteria.DocumentIds is { Count: > 0 };
    public string DocumentSelectionLabel => LocalizationService.Format("NaturalActiveDocuments", activeCriteria.DocumentIds?.Count ?? 0);
    public string DateCriteriaLabel => (activeCriteria.CreatedFrom, activeCriteria.CreatedTo) switch
    {
        (DateOnly from, DateOnly to) => LocalizationService.Format("NaturalActiveDateRange", DisplayDate(from), DisplayDate(to)),
        (DateOnly from, null) => LocalizationService.Format("NaturalActiveDateFrom", DisplayDate(from)),
        (null, DateOnly to) => LocalizationService.Format("NaturalActiveDateTo", DisplayDate(to)),
        _ => string.Empty,
    };

    public ICommand PreviousPageCommand => previousPageCommand;
    public ICommand NextPageCommand => nextPageCommand;
    public ICommand ApplyFiltersCommand => applyFiltersCommand;
    public ICommand ClearFiltersCommand => clearFiltersCommand;

    public IReadOnlyList<FilterOption> Tags { get; private set; } = [AllTags];
    public IReadOnlyList<FilterOption> Correspondents { get; private set; } = [AllCorrespondents];
    public IReadOnlyList<FilterOption> DocumentTypes { get; private set; } = [AllTypes];

    public string SearchText
    {
        get => searchText;
        set
        {
            if (SetProperty(ref searchText, value))
            {
                clearFiltersCommand.Refresh();
            }
        }
    }

    public FilterOption? SelectedTag
    {
        get => selectedTag;
        set => SetFilter(ref selectedTag, value);
    }

    public FilterOption? SelectedCorrespondent
    {
        get => selectedCorrespondent;
        set => SetFilter(ref selectedCorrespondent, value);
    }

    public FilterOption? SelectedDocumentType
    {
        get => selectedDocumentType;
        set => SetFilter(ref selectedDocumentType, value);
    }

    private bool HasSearchInput => !string.IsNullOrWhiteSpace(SearchText) || SelectedTag?.Id is not null ||
        SelectedCorrespondent?.Id is not null || SelectedDocumentType?.Id is not null || activeCriteria != EmptyCriteria;

    public async Task<InvalidConnectionField> ConnectAsync(string address, string apiToken)
    {
        if (IsBusy)
        {
            return InvalidConnectionField.None;
        }

        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var parsedServer) ||
            parsedServer.Scheme is not ("http" or "https") ||
            parsedServer.UserInfo.Length != 0 || parsedServer.Query.Length != 0 || parsedServer.Fragment.Length != 0)
        {
            SetStatus(new("InvalidServerAddress"));
            return InvalidConnectionField.Address;
        }

        var trimmedToken = apiToken.Trim();
        if (trimmedToken.Length == 0 || trimmedToken.Any(char.IsWhiteSpace))
        {
            SetStatus(new("MissingApiToken"));
            return InvalidConnectionField.Token;
        }

        if (server != parsedServer || token != trimmedToken)
        {
            PendingUploadTaskId = null;
        }

        server = parsedServer;
        token = trimmedToken;
        SetMetadata(null);
        ResetSearchInput();
        activeCriteria = EmptyCriteria;
        SetPage(0, null);
        await LoadPageAsync(1, activeCriteria);
        return InvalidConnectionField.None;
    }

    internal async Task FinishUploadAsync(Guid? pendingTaskId, bool isCompleted, int? documentId)
    {
        PendingUploadTaskId = pendingTaskId;
        if (isCompleted)
        {
            await LoadPageAsync(1, activeCriteria);
            SetStatus(new("DocumentsUploadSuccess", statusMessage));
            if (documentId is int id)
            {
                SelectedDocument = Documents.FirstOrDefault(item => item.Document.Id == id);
            }
        }
        else if (pendingTaskId is not null)
        {
            SetStatus(new("DocumentsUploadPending"));
        }
    }

    internal Task RefreshAfterTagChangesAsync() => LoadPageAsync(1, activeCriteria, refreshMetadata: true);

    internal async Task ApplyNaturalSearchAsync(DocumentSearchCriteria criteria)
    {
        await LoadPageAsync(1, criteria);
        if (activeCriteria == criteria)
        {
            SearchText = criteria.Text ?? string.Empty;
            SelectedTag = Tags.FirstOrDefault(option => option.Id == criteria.TagId) ?? AllTags;
            SelectedCorrespondent = Correspondents.FirstOrDefault(option => option.Id == criteria.CorrespondentId) ?? AllCorrespondents;
            SelectedDocumentType = DocumentTypes.FirstOrDefault(option => option.Id == criteria.DocumentTypeId) ?? AllTypes;
        }
    }

    private Task ApplyFiltersAsync() => LoadPageAsync(1, new DocumentSearchCriteria(
        string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
        SelectedTag?.Id, SelectedCorrespondent?.Id, SelectedDocumentType?.Id,
        activeCriteria.CreatedFrom, activeCriteria.CreatedTo, activeCriteria.DocumentIds));

    private Task ClearFiltersAsync()
    {
        ResetSearchInput();
        return LoadPageAsync(1, EmptyCriteria);
    }

    private void ResetSearchInput()
    {
        SearchText = string.Empty;
        SelectedTag = AllTags;
        SelectedCorrespondent = AllCorrespondents;
        SelectedDocumentType = AllTypes;
    }

    private void SetFilter(ref FilterOption? field, FilterOption? value, [CallerMemberName] string? name = null)
    {
        if (SetProperty(ref field, value, name))
        {
            clearFiltersCommand.Refresh();
        }
    }

    private async Task LoadPageAsync(int targetPage, DocumentSearchCriteria criteria, bool refreshMetadata = false)
    {
        if (IsBusy || server is not { } activeServer)
        {
            return;
        }

        IsBusy = true;
        SetStatus(new("DocumentsLoading", targetPage));

        try
        {
            if (refreshMetadata)
            {
                var refreshed = await TryLoadMetadataAsync(activeServer, token, lifetime.Token);
                if (refreshed is not null)
                {
                    SetMetadata(refreshed);
                    if (criteria.TagId is int tagId && !refreshed.Tags.ContainsKey(tagId))
                    {
                        criteria = criteria with { TagId = null };
                    }
                }
                else
                {
                    metadata = null;
                }
            }

            var pageTask = client.GetDocumentsAsync(activeServer, token, targetPage, criteria, lifetime.Token);
            var metadataTask = metadata is null && !refreshMetadata
                ? TryLoadMetadataAsync(activeServer, token, lifetime.Token)
                : Task.FromResult<PaperlessDocumentMetadata?>(metadata);

            // Observe both requests before updating the page, even if one fails.
            await Task.WhenAll(pageTask, metadataTask);
            var page = await pageTask;
            var loadedMetadata = await metadataTask;
            if (loadedMetadata != metadata)
            {
                SetMetadata(loadedMetadata);
            }

            activeCriteria = criteria;
            SetPage(targetPage, page);

            var resultMessage = page.Count == 0
                ? new LocalizedMessage(criteria == EmptyCriteria ? "DocumentsEmpty" : "SearchEmpty")
                : new LocalizedMessage("DocumentsFound", page.Results.Count, page.Count);
            SetStatus(metadata is null && page.Results.Count > 0
                ? new("DocumentsMetadataUnavailable", resultMessage)
                : resultMessage);
        }
        catch (HttpRequestException exception)
        {
            SetStatus(exception.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new("DocumentsAccessDenied"),
                HttpStatusCode.NotFound => new("DocumentsPageMissing"),
                null => new("DocumentsServerUnavailable"),
                _ => new("ServerHttpError", (int)exception.StatusCode.Value),
            });
        }
        catch (JsonException)
        {
            SetStatus(new("DocumentsInvalidResponse"));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Closing the window cancels requests in progress.
        }
        catch (OperationCanceledException)
        {
            SetStatus(new("ServerNoResponse"));
        }
        finally
        {
            if (!lifetime.IsCancellationRequested)
            {
                IsBusy = false;
            }
        }
    }

    private async Task<PaperlessDocumentMetadata?> TryLoadMetadataAsync(
        Uri activeServer, string apiToken, CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetDocumentMetadataAsync(activeServer, apiToken, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private void SetPage(int number, PaperlessDocumentPage? page)
    {
        currentPage = page;
        pageNumber = number;
        SelectedDocument = null;
        Documents = page?.Results.Select(document => new DocumentListItem(document, metadata)).ToArray() ?? [];
        NotifyPropertyChanged(nameof(PageLabel));
        NotifyPropertyChanged(nameof(HasDateCriteria));
        NotifyPropertyChanged(nameof(HasDocumentSelection));
        NotifyPropertyChanged(nameof(DocumentSelectionLabel));
        NotifyPropertyChanged(nameof(DateCriteriaLabel));
        NotifyPropertyChanged(nameof(CanSearch));
        NotifyPropertyChanged(nameof(IsConnectionRequired));
        RefreshCommands();
    }

    private void SetMetadata(PaperlessDocumentMetadata? loaded)
    {
        metadata = loaded;
        var tagId = SelectedTag?.Id;
        var correspondentId = SelectedCorrespondent?.Id;
        var documentTypeId = SelectedDocumentType?.Id;
        Tags = CreateOptions(AllTags, loaded?.Tags);
        Correspondents = CreateOptions(AllCorrespondents, loaded?.Correspondents);
        DocumentTypes = CreateOptions(AllTypes, loaded?.DocumentTypes);
        NotifyPropertyChanged(nameof(Tags));
        NotifyPropertyChanged(nameof(Correspondents));
        NotifyPropertyChanged(nameof(DocumentTypes));
        SelectedTag = Tags.FirstOrDefault(option => option.Id == tagId) ?? AllTags;
        SelectedCorrespondent = Correspondents.FirstOrDefault(option => option.Id == correspondentId) ?? AllCorrespondents;
        SelectedDocumentType = DocumentTypes.FirstOrDefault(option => option.Id == documentTypeId) ?? AllTypes;
        NotifyPropertyChanged(nameof(SelectedTag));
        NotifyPropertyChanged(nameof(SelectedCorrespondent));
        NotifyPropertyChanged(nameof(SelectedDocumentType));
    }

    private static IReadOnlyList<FilterOption> CreateOptions(FilterOption all, IReadOnlyDictionary<int, string>? names) =>
        names is null ? [all] : [all, .. names.OrderBy(item => item.Value, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => new FilterOption(item.Key, item.Value))];

    private void SetStatus(LocalizedMessage message)
    {
        statusMessage = message;
        NotifyPropertyChanged(nameof(StatusMessage));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        NotifyPropertyChanged(nameof(DocumentSelectionLabel));
        NotifyPropertyChanged(nameof(StatusMessage));
        NotifyPropertyChanged(nameof(PageLabel));
        NotifyPropertyChanged(nameof(DateCriteriaLabel));
        AllTags.RefreshName();
        AllCorrespondents.RefreshName();
        AllTypes.RefreshName();
        foreach (var document in Documents)
        {
            document.RefreshLanguage();
        }
    }

    private static string DisplayDate(DateOnly date) => LocalizationService.Format("DocumentDateFormat", date);

    private void RefreshCommands()
    {
        previousPageCommand.Refresh();
        nextPageCommand.Refresh();
        applyFiltersCommand.Refresh();
        clearFiltersCommand.Refresh();
    }

    public void Dispose()
    {
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        Preview.Dispose();
        lifetime.Cancel();
        lifetime.Dispose();
    }
}

internal sealed class FilterOption(int? id, string name, bool localized = false) : ObservableViewModel
{
    public int? Id { get; } = id;
    public string Name => localized ? LocalizationService.Text(name) : name;

    internal void RefreshName() => NotifyPropertyChanged(nameof(Name));
}

internal sealed class DocumentListItem(PaperlessDocument document, PaperlessDocumentMetadata? metadata) : ObservableViewModel
{
    public PaperlessDocument Document { get; } = document;

    public string CreatedDisplay => Document.Created is DateOnly date
        ? LocalizationService.Format("DocumentDateFormat", date)
        : LocalizationService.Text("NoDocumentDate");

    public string TypeName => Document.DocumentTypeId is int id
        ? metadata?.DocumentTypes.GetValueOrDefault(id) ?? LocalizationService.Format("DocumentTypeFallback", id)
        : LocalizationService.Text("NoType");

    public string CorrespondentName => Document.CorrespondentId is int id
        ? metadata?.Correspondents.GetValueOrDefault(id) ?? LocalizationService.Format("CorrespondentFallback", id)
        : LocalizationService.Text("NoCorrespondent");

    public string TagNames => Document.TagIds.Count == 0
        ? LocalizationService.Text("NoTags")
        : string.Join(", ", Document.TagIds.Select(id =>
            metadata?.Tags.GetValueOrDefault(id) ?? LocalizationService.Format("TagFallback", id)));

    internal void RefreshLanguage()
    {
        NotifyPropertyChanged(nameof(CreatedDisplay));
        NotifyPropertyChanged(nameof(TypeName));
        NotifyPropertyChanged(nameof(CorrespondentName));
        NotifyPropertyChanged(nameof(TagNames));
    }
}

internal enum InvalidConnectionField
{
    None,
    Address,
    Token
}
