using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    private const int MaximumPreviewBytes = 32 * 1024 * 1024;

    public Task<PaperlessDocumentPage> GetDocumentsAsync(
        Uri server,
        string token,
        CancellationToken cancellationToken = default) => GetDocumentsAsync(server, token, 1, cancellationToken);

    public Task<PaperlessDocumentPage> GetDocumentsAsync(
        Uri server,
        string token,
        int pageNumber,
        CancellationToken cancellationToken = default) =>
        GetDocumentsAsync(server, token, pageNumber, new DocumentSearchCriteria(), cancellationToken);

    public async Task<PaperlessDocumentPage> GetDocumentsAsync(
        Uri server,
        string token,
        int pageNumber,
        DocumentSearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var query = new List<string> { GetPageQuery(pageNumber), "truncate_content=true" };
        if (!string.IsNullOrWhiteSpace(criteria.Text))
        {
            // Paperless 2.x uses title_content for literal title and OCR searches.
            query.Add("title_content=" + Uri.EscapeDataString(criteria.Text.Trim()));
        }

        AddIdFilter(query, "tags__id__all", criteria.TagId, nameof(criteria.TagId));
        AddIdFilter(query, "correspondent__id", criteria.CorrespondentId, nameof(criteria.CorrespondentId));
        AddIdFilter(query, "document_type__id", criteria.DocumentTypeId, nameof(criteria.DocumentTypeId));

        var page = await GetPageAsync<PaperlessDocument>(
            server, token, "documents", string.Join('&', query), cancellationToken).ConfigureAwait(false);

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
        var correspondents = GetNamesAsync(server, token, "correspondents", cancellationToken);

        await Task.WhenAll(tags, documentTypes, correspondents).ConfigureAwait(false);
        return new PaperlessDocumentMetadata(
            await tags.ConfigureAwait(false),
            await documentTypes.ConfigureAwait(false))
        {
            Correspondents = await correspondents.ConfigureAwait(false),
        };
    }

    public Task<PaperlessDocument> GetDocumentAsync(
        Uri server,
        string token,
        int documentId,
        CancellationToken cancellationToken = default) =>
        GetJsonAsync<PaperlessDocument>(server, token, GetDocumentResource(documentId), null, cancellationToken);

    public async Task<PaperlessDocumentPreview> GetDocumentPreviewAsync(
        Uri server,
        string token,
        int documentId,
        CancellationToken cancellationToken = default)
    {
        using var download = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        download.CancelAfter(httpClient.Timeout);

        using var request = CreateGetRequest(server, token, GetDocumentResource(documentId) + "/preview", null, "*/*");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, download.Token)
            .ConfigureAwait(false);
        EnsureSuccess(response);

        long? contentLength = response.Content.Headers.ContentLength;
        if (contentLength > MaximumPreviewBytes)
        {
            throw new InvalidDataException("Paperless preview exceeds the 32 MiB limit.");
        }

        using var source = await response.Content.ReadAsStreamAsync(download.Token).ConfigureAwait(false);
        using var content = new MemoryStream((int)(contentLength ?? 0));
        var buffer = new byte[65536];
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer.AsMemory(), download.Token).ConfigureAwait(false)) != 0)
        {
            if (content.Length + bytesRead > MaximumPreviewBytes)
            {
                throw new InvalidDataException("Paperless preview exceeds the 32 MiB limit.");
            }

            content.Write(buffer, 0, bytesRead);
        }

        if (content.Length == 0)
        {
            throw new InvalidDataException("Paperless returned an empty document preview.");
        }

        return new PaperlessDocumentPreview(content.ToArray(), response.Content.Headers.ContentType?.MediaType);
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
            var page = await GetPageAsync<NamedItemResponse>(
                server, token, collection, GetPageQuery(pageNumber), cancellationToken).ConfigureAwait(false);

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
        string query,
        CancellationToken cancellationToken)
    {
        var page = await GetJsonAsync<PageResponse<T>>(server, token, collection, query, cancellationToken)
            .ConfigureAwait(false);

        if (page.Results is null || page.Count < 0)
        {
            throw new JsonException("Paperless did not return a valid list.");
        }

        return page;
    }

    private async Task<T> GetJsonAsync<T>(
        Uri server,
        string token,
        string resource,
        string? query,
        CancellationToken cancellationToken)
        where T : class
    {
        using var request = CreateGetRequest(server, token, resource, query, "application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("Paperless returned an empty JSON response.");
    }

    private static HttpRequestMessage CreateGetRequest(
        Uri server,
        string token,
        string resource,
        string? query,
        string accept)
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
            Path = $"{server.AbsolutePath.TrimEnd('/')}/api/{resource}/",
            Query = query ?? string.Empty,
        };

        var authorization = new AuthenticationHeaderValue("Token", token.Trim());
        var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.Authorization = authorization;
        return request;
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Paperless returned HTTP {(int)response.StatusCode}.",
                inner: null,
                statusCode: response.StatusCode);
        }
    }

    private static string GetPageQuery(int pageNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        return "page=" + pageNumber.ToString(CultureInfo.InvariantCulture);
    }

    private static string GetDocumentResource(int documentId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(documentId, 1);
        return "documents/" + documentId.ToString(CultureInfo.InvariantCulture);
    }

    private static void AddIdFilter(List<string> query, string key, int? id, string parameterName)
    {
        if (id is int value)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, parameterName);
            query.Add(key + "=" + value.ToString(CultureInfo.InvariantCulture));
        }
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
