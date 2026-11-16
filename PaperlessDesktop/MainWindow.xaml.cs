using System.Net.Http;
using System.Windows;
using System.Windows.Data;
using PaperlessDesktop.Services;
using PaperlessDesktop.ViewModels;

namespace PaperlessDesktop;

public partial class MainWindow : Window
{
    private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly DocumentsViewModel viewModel;

    public MainWindow()
    {
        InitializeComponent();
        viewModel = new DocumentsViewModel(new PaperlessClient(httpClient));
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

    protected override void OnClosed(EventArgs e)
    {
        viewModel.Dispose();
        httpClient.Dispose();
        base.OnClosed(e);
    }
}
