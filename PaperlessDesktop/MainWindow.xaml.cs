using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using PaperlessDesktop.Services;

namespace PaperlessDesktop;

public partial class MainWindow : Window
{
    private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly CancellationTokenSource windowLifetime = new();
    private readonly PaperlessClient paperlessClient;

    public MainWindow()
    {
        InitializeComponent();
        paperlessClient = new PaperlessClient(httpClient);
        ServerAddress.Text = Environment.GetEnvironmentVariable("PAPERLESS_SERVER_URL") ?? string.Empty;
    }

    private async void LoadDocuments_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(ServerAddress.Text.Trim(), UriKind.Absolute, out var server) ||
            server.Scheme is not ("http" or "https") ||
            server.UserInfo.Length != 0 || server.Query.Length != 0 || server.Fragment.Length != 0)
        {
            ConnectionStatus.Text = "Unesite ispravnu adresu servera koja počinje sa http:// ili https://.";
            ServerAddress.Focus();
            return;
        }

        var token = ApiToken.Password.Trim();
        if (string.IsNullOrEmpty(token) || token.Any(char.IsWhiteSpace))
        {
            ConnectionStatus.Text = "Unesite API token bez razmaka.";
            ApiToken.Focus();
            return;
        }

        Documents.ItemsSource = null;
        SetLoading(true);
        ConnectionStatus.Text = "Učitavanje prve stranice dokumenata…";

        try
        {
            var page = await paperlessClient.GetDocumentsAsync(server, token, windowLifetime.Token);
            Documents.ItemsSource = page.Results;
            ConnectionStatus.Text = page.Count == 0
                ? "Nema dokumenata za prikaz."
                : $"Prikazano {page.Results.Count} od ukupno {page.Count} dokumenata.";
        }
        catch (HttpRequestException exception)
        {
            ConnectionStatus.Text = exception.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    "Pristup dokumentima je odbijen. Provjerite API token i dozvole.",
                HttpStatusCode.NotFound => "Paperless API nije pronađen na toj adresi. Provjerite adresu servera.",
                null => "Server nije dostupan. Provjerite adresu servera i mrežnu vezu.",
                _ => $"Server je vratio grešku ({(int)exception.StatusCode.Value}). Pokušajte ponovo."
            };
        }
        catch (JsonException)
        {
            ConnectionStatus.Text = "Server nije vratio očekivanu listu. Provjerite adresu Paperless servera.";
        }
        catch (OperationCanceledException) when (windowLifetime.IsCancellationRequested)
        {
            // Closing the window cancels an in-progress request.
        }
        catch (OperationCanceledException)
        {
            ConnectionStatus.Text = "Server nije odgovorio na vrijeme. Pokušajte ponovo.";
        }
        finally
        {
            if (!windowLifetime.IsCancellationRequested)
            {
                SetLoading(false);
            }
        }
    }

    private void SetLoading(bool isLoading)
    {
        ConnectionForm.IsEnabled = !isLoading;
        LoadProgress.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnClosed(EventArgs e)
    {
        windowLifetime.Cancel();
        windowLifetime.Dispose();
        httpClient.Dispose();
        base.OnClosed(e);
    }
}
