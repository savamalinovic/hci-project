using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaperlessDesktop.Models;

namespace PaperlessDesktop.Services;

public sealed class PaperlessClient(HttpClient httpClient)
{
    public async Task<PaperlessDocumentPage> GetDocumentsAsync(
        Uri server,
        string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        if (!server.IsAbsoluteUri || server.Scheme is not ("http" or "https") ||
            server.UserInfo.Length != 0 || server.Query.Length != 0 || server.Fragment.Length != 0)
        {
            throw new ArgumentException("Enter the base HTTP or HTTPS Paperless server URL.", nameof(server));
        }

        var endpoint = new UriBuilder(server)
        {
            Path = server.AbsolutePath.TrimEnd('/') + "/api/documents/",
            Query = "page=1",
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token.Trim());

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Paperless returned HTTP {(int)response.StatusCode}.",
                inner: null,
                statusCode: response.StatusCode);
        }

        var page = await response.Content.ReadFromJsonAsync<PaperlessDocumentPage>(cancellationToken)
            .ConfigureAwait(false);

        if (page?.Results is null)
        {
            throw new JsonException("Paperless did not return a document list.");
        }

        return page;
    }
}
