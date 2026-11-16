using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using PaperlessDesktop.Services;

namespace PaperlessDesktop.ViewModels;

internal sealed class DocumentPreviewViewModel : ObservableViewModel, IDisposable
{
    private readonly PaperlessClient client;
    private readonly AsyncCommand previousPageCommand;
    private readonly AsyncCommand nextPageCommand;
    private PreviewSession? session;
    private DocumentListItem? item;
    private BitmapSource? image;
    private string extractedText = string.Empty;
    private string notice = string.Empty;
    private int pageIndex;
    private int pageCount;
    private bool isRendering;

    public DocumentPreviewViewModel(PaperlessClient client)
    {
        this.client = client;
        previousPageCommand = new AsyncCommand(
            () => !isRendering && pageIndex > 0, () => ChangePageAsync(pageIndex - 1));
        nextPageCommand = new AsyncCommand(
            () => !isRendering && pageIndex + 1 < pageCount, () => ChangePageAsync(pageIndex + 1));
    }

    public DocumentListItem? Item => item;
    public bool HasSelection => item is not null;
    public BitmapSource? Image => image;
    public string ExtractedText => extractedText;
    public bool HasExtractedText => extractedText.Length > 0;
    public string Notice => notice;
    public bool HasNotice => notice.Length > 0;
    public bool HasMultiplePages => pageCount > 1;
    public string PageLabel => $"{pageIndex + 1} / {pageCount}";
    public ICommand PreviousPageCommand => previousPageCommand;
    public ICommand NextPageCommand => nextPageCommand;

    public void Show(DocumentListItem? selected, Uri? server, string token)
    {
        StopSession();
        SetProperty(ref item, selected, nameof(Item));
        NotifyPropertyChanged(nameof(HasSelection));
        SetProperty(ref image, null, nameof(Image));
        SetExtractedText(string.Empty);
        SetNotice(string.Empty);
        pageIndex = 0;
        pageCount = 0;
        isRendering = false;
        UpdatePageNavigation();

        if (selected is null || server is null)
        {
            return;
        }

        var active = new PreviewSession();
        session = active;
        SetNotice("Učitavanje pregleda…");
        active.Operation = Task.WhenAll(
            LoadTextAsync(active, server, token, selected.Document.Id),
            LoadImageAsync(active, server, token, selected.Document.Id));
    }

    private async Task LoadTextAsync(PreviewSession active, Uri server, string token, int documentId)
    {
        var cancellationToken = active.Cancellation.Token;
        try
        {
            var document = await client.GetDocumentAsync(server, token, documentId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SetExtractedText(document.Content?.Trim() ?? string.Empty);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        {
            // A document without OCR text can still have a visual preview.
        }
    }

    private async Task LoadImageAsync(PreviewSession active, Uri server, string token, int documentId)
    {
        var cancellationToken = active.Cancellation.Token;
        try
        {
            var preview = await client.GetDocumentPreviewAsync(server, token, documentId, cancellationToken);
            active.Renderer = await DocumentPreviewRenderer.OpenAsync(preview.Content, preview.MediaType, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (active.Renderer is not { } renderer)
            {
                SetNotice("Vizuelni pregled nije dostupan za ovaj format. Tekst pročitajte ispod ako je dostupan.");
                return;
            }

            var firstImage = await renderer.RenderPageAsync(0, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SetProperty(ref image, firstImage, nameof(Image));
            pageCount = renderer.PageCount;
            UpdatePageNavigation();
            SetNotice(string.Empty);
        }
        catch (Exception error) when (IsPreviewError(error))
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SetNotice(GetPreviewErrorMessage(error));
            }
        }
    }

    private Task ChangePageAsync(int targetIndex)
    {
        if (session is not { Renderer: not null } active || isRendering)
        {
            return Task.CompletedTask;
        }

        var priorOperation = active.Operation;
        var rendering = RenderPageAsync(active, active.Renderer, targetIndex);
        active.Operation = Task.WhenAll(priorOperation, rendering);
        return rendering;
    }

    private async Task RenderPageAsync(PreviewSession active, DocumentPreviewRenderer renderer, int targetIndex)
    {
        var cancellationToken = active.Cancellation.Token;
        isRendering = true;
        UpdatePageNavigation();
        SetNotice("Učitavanje stranice pregleda…");
        try
        {
            var renderedImage = await renderer.RenderPageAsync(targetIndex, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SetProperty(ref image, renderedImage, nameof(Image));
            pageIndex = targetIndex;
            SetNotice(string.Empty);
        }
        catch (Exception error) when (IsPreviewError(error))
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SetNotice("Stranica pregleda nije učitana. Pokušajte ponovo.");
            }
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                isRendering = false;
                UpdatePageNavigation();
            }
        }
    }

    private static bool IsPreviewError(Exception error) => error is HttpRequestException or OperationCanceledException
        or IOException or InvalidDataException or FormatException or ArgumentException or NotSupportedException or COMException;

    private static string GetPreviewErrorMessage(Exception error) => error switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
            "Nemate dozvolu za pregled ovog dokumenta.",
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "Pregled ovog dokumenta nije dostupan na serveru.",
        OperationCanceledException => "Server nije na vrijeme poslao pregled. Ponovo odaberite dokument.",
        HttpRequestException => "Pregled nije preuzet. Provjerite mrežnu vezu i ponovo odaberite dokument.",
        _ => "Dokument nije moguće prikazati. Tekst pročitajte ispod ako je dostupan."
    };

    private void SetExtractedText(string text)
    {
        if (SetProperty(ref extractedText, text, nameof(ExtractedText)))
        {
            NotifyPropertyChanged(nameof(HasExtractedText));
        }
    }

    private void SetNotice(string message)
    {
        if (SetProperty(ref notice, message, nameof(Notice)))
        {
            NotifyPropertyChanged(nameof(HasNotice));
        }
    }

    private void UpdatePageNavigation()
    {
        NotifyPropertyChanged(nameof(HasMultiplePages));
        NotifyPropertyChanged(nameof(PageLabel));
        previousPageCommand.Refresh();
        nextPageCommand.Refresh();
    }

    private void StopSession()
    {
        var active = session;
        session = null;
        if (active is not null)
        {
            _ = active.CloseAsync();
        }
    }

    public void Dispose() => StopSession();

    private sealed class PreviewSession
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public DocumentPreviewRenderer? Renderer { get; set; }
        public Task Operation { get; set; } = Task.CompletedTask;

        public async Task CloseAsync()
        {
            Cancellation.Cancel();
            try
            {
                await Operation;
            }
            finally
            {
                Renderer?.Dispose();
                Cancellation.Dispose();
            }
        }
    }
}
