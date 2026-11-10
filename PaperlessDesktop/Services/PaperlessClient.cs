using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PaperlessDesktop.Models;

namespace PaperlessDesktop.Services;

public sealed class PaperlessClient(HttpClient httpClient)
{
    public Task<PaperlessDocumentPage> GetDocumentsAsync(
        Uri server,
        string token,
        CancellationToken cancellationToken = default) => GetDocumentsAsync(server, token, 1, cancellationToken);

    public async Task<PaperlessDocumentPage> GetDocumentsAsync(
        Uri server,
        string token,
        int pageNumber,
        CancellationToken cancellationToken = default)
    {
        var page = await GetPageAsync<PaperlessDocument>(server, token, "documents", pageNumber, cancellationToken)
            .ConfigureAwait(false);

        return new PaperlessDocumentPage
        {
            Count = page.Count,
            Results = page.Results,
            HasNextPage = !string.IsNullOrWhiteSpace(page.Next),
            HasPreviousPage = !string.IsNullOrWhiteSpace(page.Previous),
        };
    }

    public async Task<PaperlessDocumentMetadata> GetDocumentMetadataAsync(
        Uri server,
        string token,
        CancellationToken cancellationToken = default)
    {
        var tags = GetNamesAsync(server, token, "tags", cancellationToken);
        var documentTypes = GetNamesAsync(server, token, "document_types", cancellationToken);

        await Task.WhenAll(tags, documentTypes).ConfigureAwait(false);
        return new PaperlessDocumentMetadata(
            await tags.ConfigureAwait(false),
            await documentTypes.ConfigureAwait(false));
    }

    private async Task<IReadOnlyDictionary<int, string>> GetNamesAsync(
        Uri server,
        string token,
        string collection,
        CancellationToken cancellationToken)
    {
        var names = new Dictionary<int, string>();
        int maximumPages = 0;

        for (int pageNumber = 1; ; pageNumber++)
        {
            var page = await GetPageAsync<NamedItemResponse>(server, token, collection, pageNumber, cancellationToken)
                .ConfigureAwait(false);

            if (pageNumber == 1)
            {
                maximumPages = Math.Max(1, page.Count);
            }

            int previousCount = names.Count;
            foreach (var item in page.Results)
            {
                names[item.Id] = item.Name;
            }

            if (string.IsNullOrWhiteSpace(page.Next))
            {
                if (names.Count != page.Count)
                {
                    throw new JsonException("Paperless returned an incomplete metadata list.");
                }

                return names;
            }

            // Each additional page must introduce at least one item from the reported total.
            if (names.Count == previousCount || pageNumber >= maximumPages)
            {
                throw new JsonException("Paperless metadata pagination did not progress.");
            }
        }
    }

    private async Task<PageResponse<T>> GetPageAsync<T>(
        Uri server,
        string token,
        string collection,
        int pageNumber,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);

        if (!server.IsAbsoluteUri || server.Scheme is not ("http" or "https") ||
            server.UserInfo.Length != 0 || server.Query.Length != 0 || server.Fragment.Length != 0)
        {
            throw new ArgumentException("Enter the base HTTP or HTTPS Paperless server URL.", nameof(server));
        }

        var endpoint = new UriBuilder(server)
        {
            Path = $"{server.AbsolutePath.TrimEnd('/')}/api/{collection}/",
            Query = "page=" + pageNumber.ToString(CultureInfo.InvariantCulture),
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

        var page = await response.Content.ReadFromJsonAsync<PageResponse<T>>(cancellationToken)
            .ConfigureAwait(false);

        if (page?.Results is null || page.Count < 0)
        {
            throw new JsonException("Paperless did not return a valid list.");
        }

        return page;
    }

    private sealed class PageResponse<T>
    {
        [JsonPropertyName("count")]
        public required int Count { get; init; }

        [JsonPropertyName("results")]
        public required IReadOnlyList<T> Results { get; init; }

        [JsonPropertyName("next")]
        public string? Next { get; init; }

        [JsonPropertyName("previous")]
        public string? Previous { get; init; }
    }

    private sealed class NamedItemResponse
    {
        [JsonPropertyName("id")]
        public required int Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }
    }
}
