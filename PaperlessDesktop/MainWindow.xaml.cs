using System.Net.Http;
using System.Windows;
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

    protected override void OnClosed(EventArgs e)
    {
        viewModel.Dispose();
        httpClient.Dispose();
        base.OnClosed(e);
    }
}
