using System.Net.Http;
using System.Windows;
using System.Windows.Data;
using PaperlessDesktop.Services;
using PaperlessDesktop.ViewModels;

namespace PaperlessDesktop;

public partial class MainWindow : Window
{
    private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly HttpClient uploadHttpClient = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly HttpClient geminiHttpClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(18),
    };
    private readonly PaperlessClient client;
    private readonly DocumentsViewModel viewModel;

    public MainWindow()
    {
        InitializeComponent();
        client = new PaperlessClient(httpClient);
        viewModel = new DocumentsViewModel(client);
        DataContext = viewModel;
        ServerAddress.Text = Environment.GetEnvironmentVariable("PAPERLESS_SERVER_URL") ?? string.Empty;
    }

    private async void LoadDocuments_Click(object sender, RoutedEventArgs e)
    {
        var invalidField = await viewModel.ConnectAsync(ServerAddress.Text, ApiToken.Password);
        switch (invalidField)
        {
            case InvalidConnectionField.Address:
                ServerAddress.Focus();
                break;
            case InvalidConnectionField.Token:
                ApiToken.Focus();
                break;
        }
    }

    private void ClosePreview_Click(object sender, RoutedEventArgs e)
    {
        viewModel.SelectedDocument = null;
        DocumentViews.Focus();
    }

    private void PreviewImage_TargetUpdated(object sender, DataTransferEventArgs e) => PreviewScroll.ScrollToTop();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            var settings = new SettingsWindow(app) { Owner = this };
            settings.ShowDialog();
        }
    }

    private async void NaturalSearch_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        var model = Environment.GetEnvironmentVariable("GEMINI_MODEL");
        var gemini = string.IsNullOrWhiteSpace(apiKey) ? null : new GeminiSearchInterpreter(geminiHttpClient,
            apiKey.Trim(), string.IsNullOrWhiteSpace(model) ? "gemini-3.5-flash-lite" : model.Trim());
        var search = new NaturalSearchWindow(viewModel, gemini) { Owner = this };
        if (search.ShowDialog() == true && search.Criteria is { } criteria)
        {
            await viewModel.ApplyNaturalSearchAsync(criteria);
        }
    }

    private async void UploadDocument_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.GetActiveConnection() is { } connection)
        {
            var upload = new DocumentUploadWindow(new PaperlessClient(uploadHttpClient), connection.Server,
                connection.Token, viewModel) { Owner = this };
            upload.ShowDialog();
            await viewModel.FinishUploadAsync(upload.PendingTaskId, upload.IsCompleted, upload.ImportedDocumentId);
        }
    }

    private async void ManageTags_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.GetActiveConnection() is { } connection)
        {
            var manager = new TagManagerWindow(client, connection.Server, connection.Token) { Owner = this };
            manager.ShowDialog();
            if (manager.TagsChanged)
            {
                await viewModel.RefreshAfterTagChangesAsync();
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        viewModel.Dispose();
        geminiHttpClient.Dispose();
        uploadHttpClient.Dispose();
        httpClient.Dispose();
        base.OnClosed(e);
    }
}
