using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PaperlessDesktop.Models;

namespace PaperlessDesktop.Services;

public sealed class PaperlessClient(HttpClient httpClient)
{
    private const int MaximumPreviewBytes = 32 * 1024 * 1024;
    private const int MaximumDocumentIds = 50;
    private const int MaximumDocumentTitles = 250;
    private const int MaximumDocumentTitlePages = 10;

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

        if (criteria.CreatedFrom is DateOnly start && criteria.CreatedTo is DateOnly end && start > end)
        {
            throw new ArgumentException("The start date must be on or before the end date.", nameof(criteria));
        }

        var query = new List<string> { GetPageQuery(pageNumber), "truncate_content=true" };
        if (!string.IsNullOrWhiteSpace(criteria.Text))
        {
            // Paperless 2.x uses title_content for literal title and OCR searches.
            query.Add("title_content=" + Uri.EscapeDataString(criteria.Text.Trim()));
        }

        AddIdFilter(query, "tags__id__all", criteria.TagId, nameof(criteria.TagId));
        AddIdFilter(query, "correspondent__id", criteria.CorrespondentId, nameof(criteria.CorrespondentId));
        AddIdFilter(query, "document_type__id", criteria.DocumentTypeId, nameof(criteria.DocumentTypeId));

        if (criteria.CreatedFrom is DateOnly from)
        {
            query.Add("created__date__gte=" + from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        if (criteria.CreatedTo is DateOnly to)
        {
            query.Add("created__date__lte=" + to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        if (criteria.DocumentIds is { Count: > 0 } documentIds)
        {
            var distinctIds = documentIds.Distinct().Take(MaximumDocumentIds + 1).ToArray();
            if (distinctIds.Length > MaximumDocumentIds || distinctIds.Any(id => id <= 0))
            {
                throw new ArgumentException("Provide at most 50 distinct positive document IDs.", nameof(criteria));
            }

            query.Add("id__in=" + string.Join(',', distinctIds.Select(id => id.ToString(CultureInfo.InvariantCulture))));
        }

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

    internal async Task<bool> IsUploadedDocumentAvailableAsync(
        Uri server, string token, string checksum, int previousLatestId, CancellationToken cancellationToken)
    {
        var page = await GetPageAsync<DocumentTitleResponse>(server, token, "documents",
            "page_size=1&ordering=-id&fields=id,title&checksum__iexact=" + checksum, cancellationToken)
            .ConfigureAwait(false);
        return page.Results.Any(document => document.Id > previousLatestId);
    }

    internal async Task<IReadOnlyDictionary<int, string>> GetDocumentTitlesAsync(
        Uri server,
        string token,
        CancellationToken cancellationToken = default)
    {
        var titles = new Dictionary<int, string>();
        for (int pageNumber = 1; pageNumber <= MaximumDocumentTitlePages; pageNumber++)
        {
            var query = GetPageQuery(pageNumber) + "&page_size=50&fields=id,title&ordering=-id&truncate_content=true";
            var page = await GetPageAsync<DocumentTitleResponse>(server, token, "documents", query, cancellationToken)
                .ConfigureAwait(false);

            foreach (var document in page.Results)
            {
                if (document.Id > 0 && !string.IsNullOrWhiteSpace(document.Title))
                {
                    titles.TryAdd(document.Id, document.Title);
                    if (titles.Count == MaximumDocumentTitles)
                    {
                        return titles;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(page.Next))
            {
                break;
            }
        }

        return titles;
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

        using var request = CreateRequest(
            HttpMethod.Get, server, token, GetDocumentResource(documentId) + "/preview", null, "*/*");
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

    internal async Task<(Guid TaskId, string Checksum, int? PreviousLatestId)> UploadDocumentAsync(
        Uri server,
        string token,
        DocumentUploadRequest upload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentException.ThrowIfNullOrWhiteSpace(upload.FilePath);
        cancellationToken.ThrowIfCancellationRequested();

        using var request = CreateRequest(HttpMethod.Post, server, token, "documents/post_document", null);
        using var file = new FileStream(
            upload.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (file.Length == 0)
        {
            throw new InvalidDataException("Choose a document file that is not empty.");
        }

        // Paperless indexes the MD5 checksum of the original file.
        var checksum = Convert.ToHexString(await MD5.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
        file.Position = 0;

        int? previousLatestId = null;
        using (var snapshot = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            snapshot.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var page = await GetPageAsync<DocumentTitleResponse>(
                    server, token, "documents", "page_size=1&ordering=-id&fields=id,title", snapshot.Token)
                    .ConfigureAwait(false);
                previousLatestId = page.Results.FirstOrDefault()?.Id ?? 0;
            }
            catch (Exception error) when (error is HttpRequestException or JsonException ||
                error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // The upload still works when the document list is temporarily unavailable.
            }
        }

        var form = new MultipartFormDataContent();
        request.Content = form;
        using var document = new StreamContent(file);
        document.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(document, "document", Path.GetFileName(upload.FilePath));

        if (!string.IsNullOrWhiteSpace(upload.Title))
        {
            form.Add(new StringContent(upload.Title.Trim()), "title");
        }

        if (upload.Created is DateOnly created)
        {
            form.Add(new StringContent(created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), "created");
        }

        AddFormId(form, "correspondent", upload.CorrespondentId, nameof(upload.CorrespondentId));
        AddFormId(form, "document_type", upload.DocumentTypeId, nameof(upload.DocumentTypeId));
        foreach (int tagId in upload.TagIds ?? [])
        {
            AddFormId(form, "tags", tagId, nameof(upload.TagIds));
        }

        string response = await SendJsonAsync<string>(request, cancellationToken).ConfigureAwait(false);
        return Guid.TryParse(response, out var taskId) && taskId != Guid.Empty
            ? (taskId, checksum, previousLatestId)
            : throw new JsonException("Paperless did not return a valid upload task ID.");
    }

    public async Task<PaperlessUploadTask?> GetUploadTaskAsync(
        Uri server,
        string token,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        if (taskId == Guid.Empty)
        {
            throw new ArgumentException("An upload task ID is required.", nameof(taskId));
        }

        using var response = await GetJsonAsync<JsonDocument>(
            server, token, "tasks", $"task_id={taskId:D}", cancellationToken).ConfigureAwait(false);
        var root = response.RootElement;
        var tasks = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("results", out var results)
            ? results
            : root;
        if (tasks.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Paperless did not return a task list.");
        }

        foreach (var item in tasks.EnumerateArray())
        {
            var task = item.Deserialize<UploadTaskResponse>();
            if (task?.TaskId == taskId)
            {
                if (string.IsNullOrWhiteSpace(task.Status))
                {
                    throw new JsonException("Paperless did not return the upload task status.");
                }

                return new PaperlessUploadTask(task.Status, task.DocumentId, task.Result);
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<PaperlessTag>> GetTagsAsync(
        Uri server,
        string token,
        CancellationToken cancellationToken = default)
    {
        var tags = await GetAllItemsAsync<PaperlessTag>(
            server, token, "tags", tag => tag.Id, cancellationToken).ConfigureAwait(false);
        return [.. tags.Values];
    }

    public Task<PaperlessTag> CreateTagAsync(
        Uri server,
        string token,
        string name,
        string? color = null,
        CancellationToken cancellationToken = default) =>
        SaveTagAsync(HttpMethod.Post, server, token, "tags", name, color, cancellationToken);

    public Task<PaperlessTag> UpdateTagAsync(
        Uri server,
        string token,
        int tagId,
        string name,
        string? color = null,
        CancellationToken cancellationToken = default) =>
        SaveTagAsync(HttpMethod.Patch, server, token, GetTagResource(tagId), name, color, cancellationToken);

    public async Task DeleteTagAsync(
        Uri server,
        string token,
        int tagId,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Delete, server, token, GetTagResource(tagId), null);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
    }

    private async Task<PaperlessTag> SaveTagAsync(
        HttpMethod method,
        Uri server,
        string token,
        string resource,
        string name,
        string? color,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();
        if (name.EnumerateRunes().Count() > 128)
        {
            throw new ArgumentException("Tag names can contain up to 128 characters.", nameof(name));
        }

        var fields = new Dictionary<string, string> { ["name"] = name };
        if (!string.IsNullOrWhiteSpace(color))
        {
            color = color.Trim();
            if (color.Length != 7 || color[0] != '#' ||
                !int.TryParse(color.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _))
            {
                throw new ArgumentException("Tag color must use the #RRGGBB format.", nameof(color));
            }

            fields["color"] = color;
        }

        using var request = CreateRequest(method, server, token, resource, null);
        request.Content = JsonContent.Create(fields);
        return await SendJsonAsync<PaperlessTag>(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyDictionary<int, string>> GetNamesAsync(
        Uri server,
        string token,
        string collection,
        CancellationToken cancellationToken)
    {
        var items = await GetAllItemsAsync<NamedItemResponse>(
            server, token, collection, item => item.Id, cancellationToken).ConfigureAwait(false);
        return items.ToDictionary(item => item.Key, item => item.Value.Name);
    }

    private async Task<Dictionary<int, T>> GetAllItemsAsync<T>(
        Uri server,
        string token,
        string collection,
        Func<T, int> getId,
        CancellationToken cancellationToken)
    {
        var items = new Dictionary<int, T>();
        int maximumPages = 0;

        for (int pageNumber = 1; ; pageNumber++)
        {
            var page = await GetPageAsync<T>(
                server, token, collection, GetPageQuery(pageNumber), cancellationToken).ConfigureAwait(false);

            if (pageNumber == 1)
            {
                maximumPages = Math.Max(1, page.Count);
            }

            int previousCount = items.Count;
            foreach (var item in page.Results)
            {
                items[getId(item)] = item;
            }

            if (string.IsNullOrWhiteSpace(page.Next))
            {
                if (items.Count != page.Count)
                {
                    throw new JsonException("Paperless returned an incomplete metadata list.");
                }

                return items;
            }

            // Each additional page must introduce at least one item from the reported total.
            if (items.Count == previousCount || pageNumber >= maximumPages)
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
        using var request = CreateRequest(HttpMethod.Get, server, token, resource, query);
        return await SendJsonAsync<T>(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendJsonAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
        where T : class
    {
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("Paperless returned an empty JSON response.");
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri server,
        string token,
        string resource,
        string? query,
        string accept = "application/json")
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
        var request = new HttpRequestMessage(method, endpoint.Uri);
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

    private static string GetTagResource(int tagId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tagId, 1);
        return "tags/" + tagId.ToString(CultureInfo.InvariantCulture);
    }

    private static void AddFormId(MultipartFormDataContent form, string field, int? id, string parameterName)
    {
        if (id is int value)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, parameterName);
            form.Add(new StringContent(value.ToString(CultureInfo.InvariantCulture)), field);
        }
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

    private sealed class DocumentTitleResponse
    {
        [JsonPropertyName("id")]
        public required int Id { get; init; }

        [JsonPropertyName("title")]
        public required string Title { get; init; }
    }

    private sealed class UploadTaskResponse
    {
        [JsonPropertyName("task_id")]
        public required Guid TaskId { get; init; }

        [JsonPropertyName("status")]
        public required string Status { get; init; }

        [JsonPropertyName("related_document")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int? DocumentId { get; init; }

        [JsonPropertyName("result")]
        public string? Result { get; init; }
    }
}
