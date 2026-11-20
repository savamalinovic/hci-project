using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using PaperlessDesktop.Models;
using PaperlessDesktop.Services;

namespace PaperlessDesktop.ViewModels;

internal sealed class TagManagerViewModel : ObservableViewModel, IDisposable
{
    private readonly PaperlessClient client;
    private readonly Uri server;
    private readonly string token;
    private readonly CancellationTokenSource lifetime = new();
    private readonly AsyncCommand refreshCommand;
    private readonly AsyncCommand saveCommand;
    private IReadOnlyList<PaperlessTag> tags = [];
    private PaperlessTag? selectedTag;
    private string nameInput = string.Empty;
    private string colorInput = string.Empty;
    private LocalizedMessage? notice;
    private bool hasLoaded;
    private bool isBusy;

    public TagManagerViewModel(PaperlessClient client, Uri server, string token)
    {
        this.client = client;
        this.server = server;
        this.token = token;
        refreshCommand = new AsyncCommand(() => !IsBusy, LoadAsync);
        saveCommand = new AsyncCommand(() => CanEdit && CanSave(), SaveAsync);
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    public IReadOnlyList<PaperlessTag> Tags
    {
        get => tags;
        private set => SetProperty(ref tags, value);
    }

    public PaperlessTag? SelectedTag
    {
        get => selectedTag;
        set
        {
            if (SetProperty(ref selectedTag, value))
            {
                NameInput = value?.Name ?? string.Empty;
                ColorInput = value?.Color ?? string.Empty;
                NotifyPropertyChanged(nameof(EditorTitle));
                NotifyPropertyChanged(nameof(ColorHint));
                NotifyPropertyChanged(nameof(CanDelete));
                saveCommand.Refresh();
            }
        }
    }

    public string NameInput
    {
        get => nameInput;
        set
        {
            if (SetProperty(ref nameInput, value))
            {
                saveCommand.Refresh();
            }
        }
    }

    public string ColorInput
    {
        get => colorInput;
        set
        {
            if (SetProperty(ref colorInput, value))
            {
                saveCommand.Refresh();
            }
        }
    }

    public string Notice => notice?.ToString() ?? string.Empty;

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                NotifyPropertyChanged(nameof(CanEdit));
                NotifyPropertyChanged(nameof(CanDelete));
                refreshCommand.Refresh();
                saveCommand.Refresh();
            }
        }
    }

    public bool CanEdit => hasLoaded && !IsBusy;
    public bool CanDelete => CanEdit && SelectedTag is not null;
    public bool CanClose { get; private set; } = true;
    public bool TagsChanged { get; private set; }
    public string EditorTitle => LocalizationService.Text(SelectedTag is null ? "TagEditorNew" : "TagEditorEdit");
    public string ColorHint => LocalizationService.Text(SelectedTag is null ? "TagColorHintNew" : "TagColorHintEdit");
    public ICommand RefreshCommand => refreshCommand;
    public ICommand SaveCommand => saveCommand;

    public Task LoadAsync() => LoadAsync(null);

    private Task LoadAsync(LocalizedMessage? successNotice) => RunAsync(Operation.Load, "TagsLoading", async cancellationToken =>
    {
        var loadedTags = await client.GetTagsAsync(server, token, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        SetTags(loadedTags, SelectedTag?.Id);
        hasLoaded = true;
        SetNotice(successNotice ?? (Tags.Count == 0 ? new LocalizedMessage("TagsEmpty") : null));
    });

    public void StartNewTag()
    {
        if (!CanEdit)
        {
            return;
        }

        SelectedTag = null;
        NameInput = string.Empty;
        ColorInput = string.Empty;
        SetNotice(null);
    }

    public async Task DeleteAsync(PaperlessTag tag)
    {
        if (!CanDelete || SelectedTag?.Id != tag.Id)
        {
            return;
        }

        var deleted = false;
        await RunAsync(Operation.Delete, "TagDeleting", async cancellationToken =>
        {
            await client.DeleteTagAsync(server, token, tag.Id, cancellationToken);
            TagsChanged = true;
            cancellationToken.ThrowIfCancellationRequested();
            SetTags(Tags.Where(item => item.Id != tag.Id), null);
            deleted = true;
        });

        if (deleted)
        {
            // Paperless may also delete descendants of the selected tag.
            await LoadAsync(new("TagDeleted", tag.Name));
        }
    }

    public void Dispose()
    {
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private bool CanSave()
    {
        return SelectedTag is not { } tag || NameInput.Trim() != tag.Name ||
            (ColorInput.Trim() is { Length: > 0 } color && !string.Equals(color, tag.Color, StringComparison.OrdinalIgnoreCase));
    }

    private Task SaveAsync()
    {
        var name = NameInput.Trim();
        if (name.Length == 0)
        {
            SetNotice(new("TagMissingName"));
            return Task.CompletedTask;
        }

        if (name.EnumerateRunes().Count() > 128)
        {
            SetNotice(new("TagNameTooLong"));
            return Task.CompletedTask;
        }

        var enteredColor = ColorInput.Trim();
        if (enteredColor.Length != 0 &&
            (enteredColor.Length != 7 || enteredColor[0] != '#' || enteredColor[1..].Any(character => !char.IsAsciiHexDigit(character))))
        {
            SetNotice(new("InvalidAccentColor"));
            return Task.CompletedTask;
        }

        var color = enteredColor.Length == 0 ? null : enteredColor;
        var editingId = SelectedTag?.Id;
        return RunAsync(Operation.Save, "TagSaving", async cancellationToken =>
        {
            var savedTag = editingId is int id
                ? await client.UpdateTagAsync(server, token, id, name, color, cancellationToken)
                : await client.CreateTagAsync(server, token, name, color, cancellationToken);
            TagsChanged = true;
            cancellationToken.ThrowIfCancellationRequested();
            SetTags(Tags.Where(tag => tag.Id != savedTag.Id).Append(savedTag), savedTag.Id);
            SetNotice(new(editingId is null ? "TagCreated" : "TagUpdated", savedTag.Name));
        });
    }

    private void SetTags(IEnumerable<PaperlessTag> updated, int? selectedId)
    {
        var ordered = updated.OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        SelectedTag = null;
        Tags = ordered;
        if (selectedId is int id)
        {
            SelectedTag = ordered.FirstOrDefault(tag => tag.Id == id);
        }
    }

    private async Task RunAsync(Operation operation, string progressKey, Func<CancellationToken, Task> action)
    {
        if (IsBusy || lifetime.IsCancellationRequested)
        {
            return;
        }

        var cancellationToken = lifetime.Token;
        IsBusy = true;
        CanClose = operation == Operation.Load;
        NotifyPropertyChanged(nameof(CanClose));
        SetNotice(new(progressKey));
        try
        {
            await action(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            SetUnconfirmedResult(operation, "TagServerNoResponse");
        }
        catch (HttpRequestException exception) when (exception.StatusCode is null || (int)exception.StatusCode >= 500)
        {
            SetUnconfirmedResult(operation, "TagServerUnavailable");
        }
        catch (HttpRequestException exception)
        {
            if (exception.StatusCode == HttpStatusCode.NotFound && operation != Operation.Load)
            {
                TagsChanged = true;
            }

            SetNotice(new(exception.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "TagAccessDenied",
                HttpStatusCode.BadRequest or HttpStatusCode.Conflict when operation == Operation.Save => "TagRejected",
                HttpStatusCode.NotFound when operation != Operation.Load => "TagNotFound",
                _ => "TagRequestFailed",
            }));
        }
        catch (JsonException)
        {
            SetUnconfirmedResult(operation, "TagUnexpectedResponse");
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                CanClose = true;
                NotifyPropertyChanged(nameof(CanClose));
                IsBusy = false;
            }
        }
    }

    private void SetUnconfirmedResult(Operation operation, string messageKey)
    {
        if (operation == Operation.Load)
        {
            SetNotice(new("TagRefreshRetry", new LocalizedMessage(messageKey)));
            return;
        }

        // A write may already have reached the server even if the response was lost.
        TagsChanged = true;
        SetNotice(new("TagUnconfirmedWrite", new LocalizedMessage(messageKey)));
    }

    private void SetNotice(LocalizedMessage? message) => SetProperty(ref notice, message, nameof(Notice));

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        NotifyPropertyChanged(nameof(Notice));
        NotifyPropertyChanged(nameof(EditorTitle));
        NotifyPropertyChanged(nameof(ColorHint));
    }

    private enum Operation
    {
        Load,
        Save,
        Delete
    }
}
