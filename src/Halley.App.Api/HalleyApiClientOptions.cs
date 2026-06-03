namespace Halley.App.Api;

public sealed class HalleyApiClientOptions
{
    public const string DefaultEndpoint = "https://cloud.halleyassist.com";

    public Uri AuthBaseUri { get; init; } = new(DefaultEndpoint);

    public Uri ApiBaseUri { get; init; } = new($"{DefaultEndpoint}/api");

    public string SessionKey => AuthBaseUri.GetLeftPart(UriPartial.Path).TrimEnd('/');

    public static bool TryCreate(string? endpointInput, out HalleyApiClientOptions? options, out string? error)
    {
        options = null;
        error = null;

        var candidate = string.IsNullOrWhiteSpace(endpointInput)
            ? DefaultEndpoint
            : endpointInput.Trim();

        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = $"https://{candidate.TrimStart('/')}";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var endpointUri) || string.IsNullOrWhiteSpace(endpointUri.Host))
        {
            error = "Invalid --endpoint value. Use a host like `halleyassist.com` or a full URL like `https://cloud.halleyassist.com`.";
            return false;
        }

        if (!string.Equals(endpointUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(endpointUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "Endpoint must use http or https.";
            return false;
        }

        var host = NormalizeHost(endpointUri.Host);
        var authBasePath = NormalizeAuthBasePath(endpointUri.AbsolutePath);
        var authBaseUri = BuildBaseUri(endpointUri, host, authBasePath);
        var apiBaseUri = BuildBaseUri(endpointUri, host, CombinePath(authBasePath, "api"));

        options = new HalleyApiClientOptions
        {
            AuthBaseUri = authBaseUri,
            ApiBaseUri = apiBaseUri
        };

        return true;
    }

    private static string NormalizeHost(string host) =>
        string.Equals(host, "halleyassist.com", StringComparison.OrdinalIgnoreCase)
            ? "cloud.halleyassist.com"
            : host;

    private static string NormalizeAuthBasePath(string path)
    {
        var normalized = path.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized == "/")
        {
            return string.Empty;
        }

        normalized = normalized.TrimEnd('/');
        if (string.Equals(normalized, "/api", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (normalized.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        return normalized;
    }

    private static string CombinePath(string basePath, string segment)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return $"/{segment}";
        }

        return $"{basePath.TrimEnd('/')}/{segment.TrimStart('/')}";
    }

    private static Uri BuildBaseUri(Uri inputUri, string host, string path)
    {
        var builder = new UriBuilder(inputUri)
        {
            Host = host,
            Path = string.IsNullOrWhiteSpace(path) ? "/" : path,
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri;
    }
}
