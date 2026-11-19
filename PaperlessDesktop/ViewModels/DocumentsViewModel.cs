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
    private static readonly FilterOption AllTags = new(null, "Svi tagovi");
    private static readonly FilterOption AllCorrespondents = new(null, "Svi korespondenti");
    private static readonly FilterOption AllTypes = new(null, "Svi tipovi");
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
    private string statusMessage = "Unesite adresu Paperless servera i API token za prikaz dokumenata.";

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
    }

    public DocumentPreviewViewModel Preview { get; }
    internal Guid? PendingUploadTaskId { get; private set; }

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

    public string PageLabel => currentPage is null ? string.Empty : $"Stranica {pageNumber}";

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

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
            StatusMessage = "Unesite ispravnu adresu servera koja počinje sa http:// ili https://.";
            return InvalidConnectionField.Address;
        }

        var trimmedToken = apiToken.Trim();
        if (trimmedToken.Length == 0 || trimmedToken.Any(char.IsWhiteSpace))
        {
            StatusMessage = "Unesite API token bez razmaka.";
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
            StatusMessage = $"Dokument je uspješno obrađen. {StatusMessage}";
            if (documentId is int id)
            {
                SelectedDocument = Documents.FirstOrDefault(item => item.Document.Id == id);
            }
        }
        else if (pendingTaskId is not null)
        {
            StatusMessage = "Dokument je poslan. U prozoru Dodaj dokument možete nastaviti provjeru statusa.";
        }
    }

    internal Task RefreshAfterTagChangesAsync() => LoadPageAsync(1, activeCriteria, refreshMetadata: true);

    private Task ApplyFiltersAsync() => LoadPageAsync(1, new DocumentSearchCriteria(
        string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
        SelectedTag?.Id, SelectedCorrespondent?.Id, SelectedDocumentType?.Id));

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
        StatusMessage = $"Učitavanje stranice {targetPage}…";

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

            StatusMessage = page.Count == 0
                ? criteria == EmptyCriteria ? "Nema dokumenata za prikaz." : "Nema dokumenata za zadatu pretragu."
                : $"Prikazano {page.Results.Count} od ukupno {page.Count} dokumenata.";
            if (metadata is null && page.Results.Count > 0)
            {
                StatusMessage += " Nazivi metapodataka trenutno nisu dostupni.";
            }
        }
        catch (HttpRequestException exception)
        {
            StatusMessage = exception.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    "Pristup dokumentima je odbijen. Provjerite API token i dozvole.",
                HttpStatusCode.NotFound =>
                    "Stranica nije pronađena. Provjerite adresu servera ili ponovo učitajte dokumente.",
                null => "Server nije dostupan. Provjerite adresu servera i mrežnu vezu.",
                _ => $"Server je vratio grešku ({(int)exception.StatusCode.Value}). Pokušajte ponovo."
            };
        }
        catch (JsonException)
        {
            StatusMessage = "Server nije vratio očekivanu listu. Provjerite adresu Paperless servera.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Closing the window cancels requests in progress.
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Server nije odgovorio na vrijeme. Pokušajte ponovo.";
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
        Documents = page?.Results.Select(CreateListItem).ToArray() ?? [];
        NotifyPropertyChanged(nameof(PageLabel));
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

    private DocumentListItem CreateListItem(PaperlessDocument document)
    {
        var typeName = document.DocumentTypeId is int typeId
            ? metadata?.DocumentTypes.GetValueOrDefault(typeId) ?? $"Tip #{typeId}"
            : "Bez tipa";
        var tagNames = document.TagIds.Select(id => metadata?.Tags.GetValueOrDefault(id) ?? $"Tag #{id}");
        var correspondentName = document.CorrespondentId is int correspondentId
            ? metadata?.Correspondents.GetValueOrDefault(correspondentId) ?? $"Korespondent #{correspondentId}"
            : "Bez korespondenta";
        return new DocumentListItem(document, typeName,
            document.TagIds.Count == 0 ? "Bez tagova" : string.Join(", ", tagNames), correspondentName);
    }

    private void RefreshCommands()
    {
        previousPageCommand.Refresh();
        nextPageCommand.Refresh();
        applyFiltersCommand.Refresh();
        clearFiltersCommand.Refresh();
    }

    public void Dispose()
    {
        Preview.Dispose();
        lifetime.Cancel();
        lifetime.Dispose();
    }
}

internal sealed record FilterOption(int? Id, string Name);
internal sealed record DocumentListItem(PaperlessDocument Document, string TypeName, string TagNames, string CorrespondentName);

internal enum InvalidConnectionField
{
    None,
    Address,
    Token
}
