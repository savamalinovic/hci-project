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
    private string? noticeKey;
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
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    public DocumentListItem? Item => item;
    public bool HasSelection => item is not null;
    public BitmapSource? Image => image;
    public bool HasImage => image is not null;
    public string ExtractedText => extractedText;
    public bool HasExtractedText => extractedText.Length > 0;
    public string Notice => noticeKey is { } key ? LocalizationService.Text(key) : string.Empty;
    public bool HasNotice => noticeKey is not null;
    public bool HasMultiplePages => pageCount > 1;
    public string PageLabel => LocalizationService.Format("PreviewPageNumber", pageIndex + 1, pageCount);
    public string TypeLabel => item is null ? string.Empty : LocalizationService.Format("PreviewTypeLabel", item.TypeName);
    public string CorrespondentLabel => item is null ? string.Empty : LocalizationService.Format(
        "PreviewCorrespondentLabel", item.CorrespondentName);
    public string TagsLabel => item is null ? string.Empty : LocalizationService.Format("PreviewTagsLabel", item.TagNames);
    public ICommand PreviousPageCommand => previousPageCommand;
    public ICommand NextPageCommand => nextPageCommand;

    public void Show(DocumentListItem? selected, Uri? server, string token)
    {
        StopSession();
        SetProperty(ref item, selected, nameof(Item));
        NotifyPropertyChanged(nameof(HasSelection));
        UpdateMetadataLabels();
        SetImage(null);
        SetExtractedText(string.Empty);
        SetNotice(null);
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
        SetNotice("PreviewLoading");
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
                SetNotice("PreviewUnsupported");
                return;
            }

            var firstImage = await renderer.RenderPageAsync(0, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SetImage(firstImage);
            pageCount = renderer.PageCount;
            UpdatePageNavigation();
            SetNotice(null);
        }
        catch (Exception error) when (IsPreviewError(error))
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SetNotice(GetPreviewErrorKey(error));
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
        SetNotice("PreviewPageLoading");
        try
        {
            var renderedImage = await renderer.RenderPageAsync(targetIndex, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SetImage(renderedImage);
            pageIndex = targetIndex;
            SetNotice(null);
        }
        catch (Exception error) when (IsPreviewError(error))
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SetNotice("PreviewPageFailed");
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

    private static string GetPreviewErrorKey(Exception error) => error switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => "PreviewAccessDenied",
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "PreviewMissing",
        OperationCanceledException => "PreviewTimeout",
        HttpRequestException => "PreviewDownloadFailed",
        _ => "PreviewRenderFailed",
    };

    private void SetImage(BitmapSource? value)
    {
        if (SetProperty(ref image, value, nameof(Image)))
        {
            NotifyPropertyChanged(nameof(HasImage));
        }
    }

    private void SetExtractedText(string text)
    {
        if (SetProperty(ref extractedText, text, nameof(ExtractedText)))
        {
            NotifyPropertyChanged(nameof(HasExtractedText));
        }
    }

    private void SetNotice(string? key)
    {
        if (SetProperty(ref noticeKey, key, nameof(Notice)))
        {
            NotifyPropertyChanged(nameof(HasNotice));
        }
    }

    private void UpdateMetadataLabels()
    {
        NotifyPropertyChanged(nameof(TypeLabel));
        NotifyPropertyChanged(nameof(CorrespondentLabel));
        NotifyPropertyChanged(nameof(TagsLabel));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        NotifyPropertyChanged(nameof(Notice));
        NotifyPropertyChanged(nameof(PageLabel));
        UpdateMetadataLabels();
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

    public void Dispose()
    {
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        StopSession();
    }

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
