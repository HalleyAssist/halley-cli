using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;
using Serilog.Events;

namespace Halley.App.Api;

public sealed class HalleyApiClient(HttpClient httpClient, HalleyApiClientOptions? options = null, ILogger? logger = null) : IHalleyApiClient
{
    private static readonly JsonSerializerOptions ApiSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions LogSerializerOptions = new()
    {
        WriteIndented = false
    };

    private static readonly HashSet<string> SensitiveLogFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "password",
        "secret",
        "token"
    };

    private readonly HttpClient _httpClient = httpClient;
    private readonly HalleyApiClientOptions _options = options ?? new HalleyApiClientOptions();
    private readonly ILogger? _logger = logger;

    public Task<ApiCallResult> LoginUserAsync(UserLoginRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, _options.AuthBaseUri, "/login", null, request, null, cancellationToken);

    public Task<ApiCallResult> LoginApiKeyAsync(ApiKeyLoginRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, _options.AuthBaseUri, "/auth/api_key", null, request, null, cancellationToken);

    public Task<ApiCallResult> ListApiKeysAsync(string token, int? organisationId, CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Get,
            _options.ApiBaseUri,
            "/v1/api_keys",
            token,
            null,
            new Dictionary<string, string?> { ["organisation_id"] = organisationId?.ToString() },
            cancellationToken);

    public Task<ApiCallResult> GetApiKeyAsync(string token, string id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, _options.ApiBaseUri, $"/v1/api_keys/{Uri.EscapeDataString(id)}", token, null, null, cancellationToken);

    public Task<ApiCallResult> CreateApiKeyAsync(string token, CreateApiKeyRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, _options.ApiBaseUri, "/v1/api_keys", token, request, null, cancellationToken);

    public Task<ApiCallResult> RevokeApiKeyAsync(string token, string id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, _options.ApiBaseUri, $"/v1/api_keys/{Uri.EscapeDataString(id)}/revoke", token, null, null, cancellationToken);

    public Task<ApiCallResult> ListOrganisationsAsync(string token, ListOrganisationsQuery query, CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Get,
            _options.ApiBaseUri,
            "/v1/organisations",
            token,
            null,
            new Dictionary<string, string?>
            {
                ["offset"] = query.Offset?.ToString(),
                ["order"] = query.Order,
                ["size"] = query.Size?.ToString(),
                ["name"] = query.Name
            },
            cancellationToken);

    public Task<ApiCallResult> GetOrganisationAsync(string token, int organisationId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, _options.ApiBaseUri, $"/v1/organisations/{organisationId}", token, null, null, cancellationToken);

    public Task<ApiCallResult> CreateOrganisationAsync(string token, OrganisationWriteRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, _options.ApiBaseUri, "/v1/organisations", token, request, null, cancellationToken);

    public Task<ApiCallResult> PatchOrganisationAsync(string token, int organisationId, OrganisationWriteRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Patch, _options.ApiBaseUri, $"/v1/organisations/{organisationId}", token, request, null, cancellationToken);

    public Task<ApiCallResult> PutOrganisationAsync(string token, int organisationId, OrganisationWriteRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, _options.ApiBaseUri, $"/v1/organisations/{organisationId}", token, request, null, cancellationToken);

    public Task<ApiCallResult> DeleteOrganisationAsync(string token, int organisationId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, _options.ApiBaseUri, $"/v1/organisations/{organisationId}", token, null, null, cancellationToken);

    public Task<ApiCallResult> ListUsersAsync(string token, ListUsersQuery query, CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Get,
            _options.ApiBaseUri,
            "/v1/users",
            token,
            null,
            new Dictionary<string, string?>
            {
                ["offset"] = query.Offset?.ToString(),
                ["order"] = query.Order,
                ["size"] = query.Size?.ToString(),
                ["organisation_id"] = query.OrganisationId?.ToString()
            },
            cancellationToken);

    public Task<ApiCallResult> GetCurrentUserAsync(string token, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, _options.ApiBaseUri, "/v1/users/_me", token, null, null, cancellationToken);

    public Task<ApiCallResult> GetUserAsync(string token, string name, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, _options.ApiBaseUri, $"/v1/users/{Uri.EscapeDataString(name)}", token, null, null, cancellationToken);

    public Task<ApiCallResult> CreateUserAsync(string token, UserWriteRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, _options.ApiBaseUri, "/v1/users", token, request, null, cancellationToken);

    public Task<ApiCallResult> PatchUserAsync(string token, string name, UserWriteRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Patch, _options.ApiBaseUri, $"/v1/users/{Uri.EscapeDataString(name)}", token, request, null, cancellationToken);

    public Task<ApiCallResult> PutUserAsync(string token, string name, UserWriteRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, _options.ApiBaseUri, $"/v1/users/{Uri.EscapeDataString(name)}", token, request, null, cancellationToken);

    public Task<ApiCallResult> DeleteUserAsync(string token, string name, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, _options.ApiBaseUri, $"/v1/users/{Uri.EscapeDataString(name)}", token, null, null, cancellationToken);

    private async Task<ApiCallResult> SendAsync(
        HttpMethod method,
        Uri baseUri,
        string path,
        string? token,
        object? body,
        IReadOnlyDictionary<string, string?>? query,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildUri(baseUri, path, query);
        var requestBody = body is null ? null : JsonSerializer.Serialize(body, ApiSerializerOptions);
        var requestBodyForLog = FormatBodyForLog(requestBody);
        using var request = new HttpRequestMessage(method, requestUri);
        var requestUriText = requestUri.ToString();

        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (requestBody is not null)
        {
            request.Content = new StringContent(requestBody, Encoding.UTF8, MediaTypeNames.Application.Json);
        }

        _logger?.Debug("Sending HTTP {Method} {RequestUri}.", method.Method, requestUriText);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var rawBody = response.Content is null ? null : await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();
            var reasonPhrase = response.ReasonPhrase ?? response.StatusCode.ToString();

            var logLevel = response.IsSuccessStatusCode ? LogEventLevel.Information : LogEventLevel.Warning;
            _logger?.Write(
                logLevel,
                "HTTP {Method} {RequestUri} responded {StatusCode} {ReasonPhrase} in {ElapsedMilliseconds} ms.{NewLine}Request body: {RequestBody}{NewLine}Response body: {ResponseBody}",
                method.Method,
                requestUriText,
                (int)response.StatusCode,
                reasonPhrase,
                stopwatch.ElapsedMilliseconds,
                Environment.NewLine,
                requestBodyForLog,
                FormatBodyForLog(rawBody));

            return new ApiCallResult(response.StatusCode, ParseJson(rawBody), string.IsNullOrWhiteSpace(rawBody) ? null : rawBody);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _logger?.Error(
                ex,
                "HTTP {Method} {RequestUri} failed after {ElapsedMilliseconds} ms.{NewLine}Request body: {RequestBody}",
                method.Method,
                requestUriText,
                stopwatch.ElapsedMilliseconds,
                Environment.NewLine,
                requestBodyForLog);
            throw;
        }
    }

    private static JsonNode? ParseJson(string? rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(rawBody);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FormatBodyForLog(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "<empty>";
        }

        try
        {
            var node = JsonNode.Parse(body);
            RedactSensitiveFields(node);
            return node?.ToJsonString(LogSerializerOptions) ?? "<empty>";
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static void RedactSensitiveFields(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var property in jsonObject.ToList())
                {
                    if (SensitiveLogFields.Contains(property.Key))
                    {
                        jsonObject[property.Key] = "[redacted]";
                        continue;
                    }

                    RedactSensitiveFields(property.Value);
                }

                break;

            case JsonArray jsonArray:
                foreach (var item in jsonArray)
                {
                    RedactSensitiveFields(item);
                }

                break;
        }
    }

    private static Uri BuildUri(Uri baseUri, string path, IReadOnlyDictionary<string, string?>? query)
    {
        var builder = new UriBuilder(baseUri);
        var basePath = builder.Path.TrimEnd('/');
        var relativePath = path.TrimStart('/');
        builder.Path = string.IsNullOrWhiteSpace(basePath)
            ? $"/{relativePath}"
            : $"{basePath}/{relativePath}";

        if (query is null || query.Count == 0)
        {
            builder.Query = string.Empty;
            return builder.Uri;
        }

        var parameters = query
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
            .Select(entry => $"{Uri.EscapeDataString(entry.Key)}={Uri.EscapeDataString(entry.Value!)}");

        builder.Query = string.Join("&", parameters);
        return builder.Uri;
    }
}
