using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using PaperlessDesktop.Models;
using PaperlessDesktop.Services;

namespace PaperlessDesktop.ViewModels;

internal sealed class DocumentsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly PaperlessClient client;
    private readonly CancellationTokenSource lifetime = new();
    private readonly PageCommand previousPageCommand;
    private readonly PageCommand nextPageCommand;
    private Uri? server;
    private string token = string.Empty;
    private PaperlessDocumentMetadata? metadata;
    private PaperlessDocumentPage? currentPage;
    private int pageNumber;
    private IReadOnlyList<DocumentListItem> documents = [];
    private DocumentListItem? selectedDocument;
    private bool isBusy;
    private string statusMessage = "Unesite adresu Paperless servera i API token za prikaz dokumenata.";

    public DocumentsViewModel(PaperlessClient client)
    {
        this.client = client;
        previousPageCommand = new PageCommand(
            () => !IsBusy && currentPage?.HasPreviousPage == true,
            () => LoadPageAsync(pageNumber - 1));
        nextPageCommand = new PageCommand(
            () => !IsBusy && currentPage?.HasNextPage == true,
            () => LoadPageAsync(pageNumber + 1));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<DocumentListItem> Documents
    {
        get => documents;
        private set => SetProperty(ref documents, value);
    }

    public DocumentListItem? SelectedDocument
    {
        get => selectedDocument;
        set => SetProperty(ref selectedDocument, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            SetProperty(ref isBusy, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanConnect)));
            RefreshNavigation();
        }
    }

    public bool CanConnect => !IsBusy;

    public string PageLabel => currentPage is null ? string.Empty : $"Stranica {pageNumber}";

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public ICommand PreviousPageCommand => previousPageCommand;
    public ICommand NextPageCommand => nextPageCommand;

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

        server = parsedServer;
        token = trimmedToken;
        metadata = null;
        SetPage(0, null);
        await LoadPageAsync(1);
        return InvalidConnectionField.None;
    }

    private async Task LoadPageAsync(int targetPage)
    {
        if (IsBusy || server is not { } activeServer)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"Učitavanje stranice {targetPage}…";

        try
        {
            var pageTask = client.GetDocumentsAsync(activeServer, token, targetPage, lifetime.Token);
            var metadataTask = metadata is null
                ? TryLoadMetadataAsync(activeServer, token, lifetime.Token)
                : Task.FromResult<PaperlessDocumentMetadata?>(metadata);

            // Observe both requests before updating the page, even if one fails.
            await Task.WhenAll(pageTask, metadataTask);
            var page = await pageTask;
            metadata = await metadataTask;
            SetPage(targetPage, page);

            StatusMessage = page.Count == 0
                ? "Nema dokumenata za prikaz."
                : $"Prikazano {page.Results.Count} od ukupno {page.Count} dokumenata.";
            if (metadata is null && page.Results.Count > 0)
            {
                StatusMessage += " Nazivi tagova i tipova trenutno nisu dostupni.";
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PageLabel)));
        RefreshNavigation();
    }

    private DocumentListItem CreateListItem(PaperlessDocument document)
    {
        var typeName = document.DocumentTypeId is int typeId
            ? metadata?.DocumentTypes.GetValueOrDefault(typeId) ?? $"Tip #{typeId}"
            : "Bez tipa";
        var tagNames = document.TagIds.Select(id => metadata?.Tags.GetValueOrDefault(id) ?? $"Tag #{id}");
        return new DocumentListItem(document, typeName, document.TagIds.Count == 0 ? "Bez tagova" : string.Join(", ", tagNames));
    }

    private void RefreshNavigation()
    {
        previousPageCommand.Refresh();
        nextPageCommand.Refresh();
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void Dispose()
    {
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private sealed class PageCommand(Func<bool> canExecute, Func<Task> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute();

        public async void Execute(object? parameter)
        {
            if (canExecute())
            {
                await execute();
            }
        }

        public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed record DocumentListItem(PaperlessDocument Document, string TypeName, string TagNames);

internal enum InvalidConnectionField
{
    None,
    Address,
    Token
}
