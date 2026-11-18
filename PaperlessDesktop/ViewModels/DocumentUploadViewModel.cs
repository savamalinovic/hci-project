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
    private string status = "Izaberite fajl za učitavanje.";
    private bool isBusy;
    private bool isSending;
    private Guid? taskId;

    public DocumentUploadViewModel(
        PaperlessClient client, Uri server, string token, IReadOnlyList<FilterOption> tags,
        IReadOnlyList<FilterOption> correspondents, IReadOnlyList<FilterOption> documentTypes, Guid? pendingTaskId)
    {
        this.client = client;
        this.server = server;
        this.token = token;
        taskId = pendingTaskId;
        if (taskId is not null)
        {
            Status = "Možete nastaviti provjeru statusa prethodno poslanog dokumenta.";
        }

        Tags = tags.Where(tag => tag.Id.HasValue).Select(tag => new UploadTagOption(tag.Id!.Value, tag.Name)).ToArray();
        Correspondents = [new FilterOption(null, "Bez korespondenta"), .. correspondents.Where(option => option.Id.HasValue)];
        DocumentTypes = [new FilterOption(null, "Bez tipa"), .. documentTypes.Where(option => option.Id.HasValue)];
        SelectedCorrespondent = Correspondents[0];
        SelectedDocumentType = DocumentTypes[0];
        submitCommand = new AsyncCommand(() => CanEdit && filePath is not null, SubmitAsync);
        checkStatusCommand = new AsyncCommand(() => CanCheckStatus, CheckStatusAsync);
    }

    public IReadOnlyList<UploadTagOption> Tags { get; }
    public IReadOnlyList<FilterOption> Correspondents { get; }
    public IReadOnlyList<FilterOption> DocumentTypes { get; }
    public FilterOption? SelectedCorrespondent { get; set; }
    public FilterOption? SelectedDocumentType { get; set; }
    public string CreatedText { get; set; } = string.Empty;

    public string FileName => filePath is null
        ? taskId is null ? "Nijedan fajl nije izabran" : "Prethodno poslani dokument"
        : Path.GetFileName(filePath);
    public bool IsBusy => isBusy;
    public bool CanEdit => !isBusy && taskId is null && !IsCompleted;
    public bool CanClose => !isSending;
    public bool CanCheckStatus => !isBusy && taskId is not null && !IsCompleted;
    public bool IsCompleted { get; private set; }
    public Guid? PendingTaskId => IsCompleted ? null : taskId;
    public int? ImportedDocumentId { get; private set; }
    public ICommand SubmitCommand => submitCommand;
    public ICommand CheckStatusCommand => checkStatusCommand;

    public string Title
    {
        get => title;
        set => SetProperty(ref title, value);
    }

    public string Status
    {
        get => status;
        private set => SetProperty(ref status, value);
    }

    public void SelectFile(string selectedPath)
    {
        var priorName = filePath is null ? null : Path.GetFileNameWithoutExtension(filePath);
        filePath = selectedPath;
        if (string.IsNullOrWhiteSpace(Title) || Title == priorName)
        {
            Title = Path.GetFileNameWithoutExtension(selectedPath);
        }

        NotifyPropertyChanged(nameof(FileName));
        Status = "Pregledajte metapodatke pa pošaljite dokument.";
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
                Status = "Unesite datum u formatu GGGG-MM-DD ili dan.mjesec.godina.";
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
        Status = "Slanje dokumenta…";

        try
        {
            taskId = await client.UploadDocumentAsync(server, token, request, lifetime.Token);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or OperationCanceledException or ArgumentException)
        {
            if (!lifetime.IsCancellationRequested)
            {
                Status = GetUploadError(error);
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
            if (taskId is not null)
            {
                await CheckStatusAsync();
            }
        }
    }

    private async Task CheckStatusAsync()
    {
        if (taskId is not Guid id)
        {
            return;
        }

        isBusy = true;
        UpdateState();
        Status = "Dokument je primljen. Čeka se obrada na serveru…";

        using var tracking = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        tracking.CancelAfter(TimeSpan.FromMinutes(1));
        try
        {
            while (true)
            {
                var task = await client.GetUploadTaskAsync(server, token, id, tracking.Token);
                tracking.Token.ThrowIfCancellationRequested();
                switch (task?.Status.ToUpperInvariant())
                {
                    case "SUCCESS":
                        ImportedDocumentId = task.DocumentId;
                        IsCompleted = true;
                        Status = "Dokument je uspješno obrađen.";
                        return;
                    case "FAILURE":
                    case "REVOKED":
                        taskId = null;
                        Status = task.Result?.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true
                            ? "Ovaj dokument već postoji u Paperlessu."
                            : "Paperless nije uspio obraditi dokument. Provjerite fajl i pokušajte ponovo.";
                        return;
                    case "STARTED":
                        Status = "Paperless obrađuje dokument…";
                        break;
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
            Status = "Obrada još traje. Možete ponovo provjeriti status.";
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        {
            Status = "Status obrade trenutno nije dostupan. Ponovo provjerite status.";
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

    private bool ValidateFile(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 0)
            {
                return true;
            }

            Status = "Izabrani fajl je prazan.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Status = "Izabrani fajl nije dostupan. Izaberite fajl ponovo.";
        }

        return false;
    }

    private static string GetUploadError(Exception error) => error switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
            "Nemate dozvolu za slanje dokumenata.",
        HttpRequestException { StatusCode: HttpStatusCode.RequestEntityTooLarge } =>
            "Fajl je veći nego što server dozvoljava.",
        HttpRequestException { StatusCode: HttpStatusCode.UnsupportedMediaType } =>
            "Server ne podržava format izabranog fajla.",
        HttpRequestException { StatusCode: HttpStatusCode.BadRequest } =>
            "Server je odbio fajl ili metapodatke. Provjerite unos.",
        IOException or UnauthorizedAccessException => "Izabrani fajl se ne može pročitati.",
        _ => "Server nije potvrdio prijem. Provjerite listu dokumenata prije ponovnog slanja."
    };

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
        lifetime.Cancel();
        lifetime.Dispose();
    }
}

internal sealed class UploadTagOption(int id, string name)
{
    public int Id { get; } = id;
    public string Name { get; } = name;
    public bool IsSelected { get; set; }
}
