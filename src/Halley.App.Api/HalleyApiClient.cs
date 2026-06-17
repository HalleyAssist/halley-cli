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

    private static readonly HashSet<string> SensitiveLogHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "cookie",
        "set-cookie",
        "x-api-key"
    };

    private readonly HttpClient _httpClient = httpClient;
    private readonly HalleyApiClientOptions _options = options ?? new HalleyApiClientOptions();
    private readonly ILogger? _logger = logger;

    public Task<ApiCallResult> LoginUserAsync(UserLoginRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, _options.AuthBaseUri, "/login", null, request, null, cancellationToken);

    public Task<ApiCallResult> LoginApiKeyAsync(ApiKeyLoginRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, _options.AuthBaseUri, "/auth/api_key", null, request, null, cancellationToken);

    public Task<ApiCallResult> CreateCallRequestAsync(string token, CallRequestCreateRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, _options.ApiBaseUri, "/v1/call_requests", token, request, null, cancellationToken);

    public Task<ApiCallResult> GetCallRequestAsync(string token, string callRequestUuid, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, _options.ApiBaseUri, $"/v1/call_requests/{Uri.EscapeDataString(callRequestUuid)}", token, null, null, cancellationToken);

    public Task<ApiCallResult> GetCallResultAsync(string token, string callResultUuid, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, _options.ApiBaseUri, $"/v1/call_results/{Uri.EscapeDataString(callResultUuid)}", token, null, null, cancellationToken);

    public Task<ApiCallResult> ListCallResultsAsync(string token, ListCallResultsQuery query, CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Get,
            _options.ApiBaseUri,
            "/v1/call_results",
            token,
            null,
            new Dictionary<string, string?>
            {
                ["offset"] = query.Offset?.ToString(),
                ["order"] = query.Order,
                ["size"] = query.Size?.ToString(),
                ["uuid"] = query.Uuid
            },
            cancellationToken);

    public Task<ApiCallResult> ListCallResultsForRequestAsync(string token, string callRequestUuid, CancellationToken cancellationToken = default) =>
        ListCallResultsAsync(token, new ListCallResultsQuery
        {
            Uuid = callRequestUuid,
            Order = "created_at DESC"
        }, cancellationToken);

    public Task<ApiCallResult> DeleteCallResultAsync(string token, string callResultUuid, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, _options.ApiBaseUri, $"/v1/call_results/{Uri.EscapeDataString(callResultUuid)}", token, null, null, cancellationToken);

    public Task<ApiCallResult> ListCallTemplatesAsync(string token, ListCallTemplatesQuery query, CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Get,
            _options.ApiBaseUri,
            "/v1/call_templates",
            token,
            null,
            new Dictionary<string, string?>
            {
                ["offset"] = query.Offset?.ToString(),
                ["order"] = query.Order,
                ["size"] = query.Size?.ToString(),
                ["organisation_id"] = query.OrganisationId?.ToString(),
                ["for_organisation_id"] = query.ForOrganisationId?.ToString(),
                ["all_versions"] = query.AllVersions?.ToString()?.ToLowerInvariant(),
                ["uuid"] = query.Uuid
            },
            cancellationToken);

    public Task<ApiCallResult> GetCallTemplateAsync(string token, string templateReference, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, _options.ApiBaseUri, $"/v1/call_templates/{Uri.EscapeDataString(templateReference)}", token, null, null, cancellationToken);

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

        if (_logger?.IsEnabled(LogEventLevel.Debug) == true)
        {
            _logger.Debug("HTTP request:{NewLine}{RequestDump}", Environment.NewLine, FormatRequestForDebugLog(request, requestBodyForLog));
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var rawBody = response.Content is null ? null : await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();
            var reasonPhrase = response.ReasonPhrase ?? response.StatusCode.ToString();
            var responseBodyForLog = FormatBodyForLog(rawBody);

            var logLevel = response.IsSuccessStatusCode ? LogEventLevel.Information : LogEventLevel.Warning;
            _logger?.Write(
                logLevel,
                "HTTP {Method} {RequestUri} responded {StatusCode} {ReasonPhrase} in {ElapsedMilliseconds} ms.{NewLine}Request body: {RequestBody}",
                method.Method,
                requestUriText,
                (int)response.StatusCode,
                reasonPhrase,
                stopwatch.ElapsedMilliseconds,
                Environment.NewLine,
                requestBodyForLog);

            if (_logger?.IsEnabled(logLevel) == true)
            {
                _logger.Write(logLevel, "HTTP response:{NewLine}{ResponseDump}", Environment.NewLine, FormatResponseForDebugLog(response, responseBodyForLog));
            }

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

    private static string FormatRequestForDebugLog(HttpRequestMessage request, string requestBodyForLog)
    {
        var builder = new StringBuilder();
        var requestUri = request.RequestUri;
        var pathAndQuery = requestUri?.PathAndQuery ?? "/";
        var httpVersion = FormatHttpVersion(request.Version);

        builder.Append("> ")
            .Append(request.Method.Method)
            .Append(' ')
            .Append(pathAndQuery)
            .Append(' ')
            .Append(httpVersion)
            .AppendLine();

        if (requestUri is not null)
        {
            builder.Append("> Host: ")
                .Append(requestUri.IsDefaultPort ? requestUri.Host : requestUri.Authority)
                .AppendLine();
        }

        AppendHeaders(
            builder,
            request.Headers,
            ">",
            skipHeaderNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Host" });
        if (request.Content is not null)
        {
            AppendHeaders(builder, request.Content.Headers, ">");
        }

        AppendBody(builder, ">", requestBodyForLog);
        return builder.ToString().TrimEnd();
    }

    private static string FormatResponseForDebugLog(HttpResponseMessage response, string responseBodyForLog)
    {
        var builder = new StringBuilder();
        var httpVersion = FormatHttpVersion(response.Version);
        var reasonPhrase = response.ReasonPhrase ?? response.StatusCode.ToString();

        builder.Append("< ")
            .Append(httpVersion)
            .Append(' ')
            .Append((int)response.StatusCode)
            .Append(' ')
            .Append(reasonPhrase)
            .AppendLine();

        AppendHeaders(builder, response.Headers, "<");
        if (response.Content is not null)
        {
            AppendHeaders(builder, response.Content.Headers, "<");
        }

        AppendBody(builder, "<", responseBodyForLog);
        return builder.ToString().TrimEnd();
    }

    private static string FormatHttpVersion(Version version) => $"HTTP/{version.Major}.{version.Minor}";

    private static void AppendHeaders(
        StringBuilder builder,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers,
        string prefix,
        ISet<string>? skipHeaderNames = null)
    {
        foreach (var header in headers.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (skipHeaderNames?.Contains(header.Key) == true)
            {
                continue;
            }

            builder.Append(prefix)
                .Append(' ')
                .Append(header.Key)
                .Append(": ")
                .Append(FormatHeaderValues(header.Key, header.Value))
                .AppendLine();
        }
    }

    private static string FormatHeaderValues(string headerName, IEnumerable<string> values)
    {
        if (SensitiveLogHeaders.Contains(headerName))
        {
            return "[redacted]";
        }

        return string.Join(", ", values);
    }

    private static void AppendBody(StringBuilder builder, string prefix, string body)
    {
        builder.Append(prefix).AppendLine();

        foreach (var line in body.ReplaceLineEndings("\n").Split('\n'))
        {
            builder.Append(prefix)
                .Append(' ')
                .Append(line)
                .AppendLine();
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
