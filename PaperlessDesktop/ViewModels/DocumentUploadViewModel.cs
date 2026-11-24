using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Input;
using PaperlessDesktop.Models;
using PaperlessDesktop.Services;

namespace PaperlessDesktop.ViewModels;

internal sealed class DocumentUploadViewModel : ObservableViewModel, IDisposable
{
    private readonly PaperlessClient client;
    private readonly Uri server;
    private readonly string token;
    private readonly CancellationTokenSource lifetime = new();
    private readonly AsyncCommand submitCommand;
    private readonly AsyncCommand checkStatusCommand;
    private string? filePath;
    private string title = string.Empty;
    private string statusKey = "UploadChooseFile";
    private bool isBusy;
    private bool isSending;
    private PendingUpload? pendingUpload;

    public DocumentUploadViewModel(
        PaperlessClient client, Uri server, string token, IReadOnlyList<FilterOption> tags,
        IReadOnlyList<FilterOption> correspondents, IReadOnlyList<FilterOption> documentTypes, PendingUpload? previousUpload)
    {
        this.client = client;
        this.server = server;
        this.token = token;
        pendingUpload = previousUpload;
        if (pendingUpload is not null)
        {
            statusKey = "UploadResumeStatus";
        }

        Tags = tags.Where(tag => tag.Id.HasValue).Select(tag => new UploadTagOption(tag.Id!.Value, tag.Name)).ToArray();
        Correspondents = [new FilterOption(null, "NoCorrespondent", localized: true), .. correspondents.Where(option => option.Id.HasValue)];
        DocumentTypes = [new FilterOption(null, "NoType", localized: true), .. documentTypes.Where(option => option.Id.HasValue)];
        SelectedCorrespondent = Correspondents[0];
        SelectedDocumentType = DocumentTypes[0];
        submitCommand = new AsyncCommand(() => CanEdit && filePath is not null, SubmitAsync);
        checkStatusCommand = new AsyncCommand(() => CanCheckStatus, CheckStatusAsync);
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    public IReadOnlyList<UploadTagOption> Tags { get; }
    public IReadOnlyList<FilterOption> Correspondents { get; }
    public IReadOnlyList<FilterOption> DocumentTypes { get; }
    public FilterOption? SelectedCorrespondent { get; set; }
    public FilterOption? SelectedDocumentType { get; set; }
    public string CreatedText { get; set; } = string.Empty;

    public string FileName => filePath is null
        ? LocalizationService.Text(pendingUpload is null ? "UploadNoFile" : "UploadPreviousFile")
        : Path.GetFileName(filePath);
    public bool IsBusy => isBusy;
    public bool CanEdit => !isBusy && pendingUpload is null && !IsCompleted;
    public bool CanClose => !isSending;
    public bool CanCheckStatus => !isBusy && pendingUpload is not null && !IsCompleted;
    public bool IsCompleted { get; private set; }
    public PendingUpload? Pending => IsCompleted ? null : pendingUpload;
    public ICommand SubmitCommand => submitCommand;
    public ICommand CheckStatusCommand => checkStatusCommand;

    public string Title
    {
        get => title;
        set => SetProperty(ref title, value);
    }

    public string Status => LocalizationService.Text(statusKey);

    public void SelectFile(string selectedPath)
    {
        var priorName = filePath is null ? null : Path.GetFileNameWithoutExtension(filePath);
        filePath = selectedPath;
        if (string.IsNullOrWhiteSpace(Title) || Title == priorName)
        {
            Title = Path.GetFileNameWithoutExtension(selectedPath);
        }

        NotifyPropertyChanged(nameof(FileName));
        SetStatus("UploadReviewMetadata");
        UpdateState();
    }

    private async Task SubmitAsync()
    {
        if (filePath is not { } path || !ValidateFile(path))
        {
            return;
        }

        DateOnly? created = null;
        if (!string.IsNullOrWhiteSpace(CreatedText))
        {
            if (!DateOnly.TryParseExact(CreatedText.Trim(), ["yyyy-MM-dd", "d.M.yyyy"], CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsedDate))
            {
                SetStatus("UploadInvalidDate");
                return;
            }

            created = parsedDate;
        }

        var request = new DocumentUploadRequest(path, Title.Trim(), created,
            SelectedCorrespondent?.Id, SelectedDocumentType?.Id,
            Tags.Where(tag => tag.IsSelected).Select(tag => tag.Id).ToArray());

        isBusy = true;
        isSending = true;
        UpdateState();
        SetStatus("UploadSending");

        try
        {
            var receipt = await client.UploadDocumentAsync(server, token, request, lifetime.Token);
            pendingUpload = new PendingUpload(receipt.TaskId, receipt.Checksum, receipt.PreviousLatestId);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or OperationCanceledException or ArgumentException)
        {
            if (!lifetime.IsCancellationRequested)
            {
                SetStatus(GetUploadErrorKey(error));
            }
        }
        finally
        {
            isSending = false;
            isBusy = false;
        }

        if (!lifetime.IsCancellationRequested)
        {
            UpdateState();
            if (pendingUpload is not null)
            {
                await CheckStatusAsync();
            }
        }
    }

    private async Task CheckStatusAsync()
    {
        if (pendingUpload is not { } upload)
        {
            return;
        }

        isBusy = true;
        UpdateState();
        SetStatus("UploadAccepted");

        using var tracking = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        tracking.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            while (true)
            {
                PaperlessUploadTask? task = null;
                if (upload.CanReadTask)
                {
                    try
                    {
                        using var taskCheck = CancellationTokenSource.CreateLinkedTokenSource(tracking.Token);
                        taskCheck.CancelAfter(TimeSpan.FromSeconds(4));
                        task = await client.GetUploadTaskAsync(server, token, upload.TaskId, taskCheck.Token);
                    }
                    catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    {
                        pendingUpload = upload = upload with { CanReadTask = false };
                    }
                    catch (Exception error) when (error is HttpRequestException or JsonException ||
                        error is OperationCanceledException && !tracking.IsCancellationRequested)
                    {
                        // A temporary task API failure does not prevent checking the list.
                    }
                }
                tracking.Token.ThrowIfCancellationRequested();
                switch (task?.Status.ToUpperInvariant())
                {
                    case "FAILURE":
                    case "REVOKED":
                        pendingUpload = null;
                        SetStatus(task.Result?.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true
                            ? "UploadDuplicate"
                            : "UploadProcessingFailed");
                        return;
                    case "STARTED":
                        SetStatus("UploadProcessing");
                        break;
                }

                if (await IsDocumentAvailableAsync(upload, task, tracking.Token))
                {
                    IsCompleted = true;
                    SetStatus("UploadProcessed");
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), tracking.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // The dialog was closed. Paperless keeps processing the accepted file.
        }
        catch (OperationCanceledException) when (tracking.IsCancellationRequested)
        {
            SetStatus("UploadStillProcessing");
        }
        finally
        {
            if (!lifetime.IsCancellationRequested)
            {
                isBusy = false;
                UpdateState();
            }
        }
    }

    private async Task<bool> IsDocumentAvailableAsync(
        PendingUpload upload, PaperlessUploadTask? task, CancellationToken cancellationToken)
    {
        try
        {
            using var listCheck = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            listCheck.CancelAfter(TimeSpan.FromSeconds(5));

            if (task?.DocumentId is int documentId && documentId > 0 &&
                string.Equals(task.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                var page = await client.GetDocumentsAsync(server, token, 1,
                    new DocumentSearchCriteria(DocumentIds: [documentId]), listCheck.Token);
                return page.Results.Any(document => document.Id == documentId);
            }

            return upload.PreviousLatestId is int previousLatestId &&
                await client.IsUploadedDocumentAvailableAsync(
                    server, token, upload.Checksum, previousLatestId, listCheck.Token);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private bool ValidateFile(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 0)
            {
                return true;
            }

            SetStatus("UploadEmptyFile");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetStatus("UploadFileUnavailable");
        }

        return false;
    }

    private static string GetUploadErrorKey(Exception error) => error switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => "UploadAccessDenied",
        HttpRequestException { StatusCode: HttpStatusCode.RequestEntityTooLarge } => "UploadFileTooLarge",
        HttpRequestException { StatusCode: HttpStatusCode.UnsupportedMediaType } => "UploadFileTypeUnsupported",
        HttpRequestException { StatusCode: HttpStatusCode.BadRequest } => "UploadRejected",
        IOException or UnauthorizedAccessException => "UploadUnreadableFile",
        _ => "UploadUnconfirmed",
    };

    private void SetStatus(string key) => SetProperty(ref statusKey, key, nameof(Status));

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        NotifyPropertyChanged(nameof(Status));
        NotifyPropertyChanged(nameof(FileName));
        Correspondents[0].RefreshName();
        DocumentTypes[0].RefreshName();
    }

    private void UpdateState()
    {
        NotifyPropertyChanged(nameof(IsBusy));
        NotifyPropertyChanged(nameof(FileName));
        NotifyPropertyChanged(nameof(CanEdit));
        NotifyPropertyChanged(nameof(CanClose));
        NotifyPropertyChanged(nameof(CanCheckStatus));
        NotifyPropertyChanged(nameof(IsCompleted));
        submitCommand.Refresh();
        checkStatusCommand.Refresh();
    }

    public void Dispose()
    {
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        lifetime.Cancel();
        lifetime.Dispose();
    }
}

internal sealed record PendingUpload(Guid TaskId, string Checksum, int? PreviousLatestId, bool CanReadTask = true);

internal sealed class UploadTagOption(int id, string name)
{
    public int Id { get; } = id;
    public string Name { get; } = name;
    public bool IsSelected { get; set; }
}
