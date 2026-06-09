using System.Text.Json;
using System.Text.Json.Nodes;

namespace Halley.App.Api;

public sealed record SessionRecord(string Token, string AuthType, DateTimeOffset SavedAt);

public interface ISessionStore
{
    string SessionPath { get; }

    Task<SessionRecord?> LoadAsync(string endpointKey, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, SessionRecord>> LoadAllAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(string endpointKey, SessionRecord session, CancellationToken cancellationToken = default);

    Task EnsureExistsAsync(CancellationToken cancellationToken = default);
}

public sealed class FileSessionStore(string? sessionPath = null) : ISessionStore
{
    private static readonly string DefaultSessionKey = new HalleyApiClientOptions().SessionKey;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string SessionPath { get; } = sessionPath ?? ResolveDefaultSessionPath();

    public async Task<SessionRecord?> LoadAsync(string endpointKey, CancellationToken cancellationToken = default)
    {
        var document = await LoadDocumentAsync(cancellationToken);
        return document.Sessions.TryGetValue(endpointKey, out var session) ? session : null;
    }

    public async Task<IReadOnlyDictionary<string, SessionRecord>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        var document = await LoadDocumentAsync(cancellationToken);
        return new Dictionary<string, SessionRecord>(document.Sessions, StringComparer.Ordinal);
    }

    public async Task SaveAsync(string endpointKey, SessionRecord session, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(SessionPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var document = await LoadDocumentAsync(cancellationToken);
        document.Sessions[endpointKey] = session;

        var json = JsonSerializer.Serialize(document, SerializerOptions);
        await File.WriteAllTextAsync(SessionPath, json, cancellationToken);
    }

    public async Task EnsureExistsAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(SessionPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(SessionPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(new SessionStoreDocument(), SerializerOptions);
        await File.WriteAllTextAsync(SessionPath, json, cancellationToken);
    }

    private async Task<SessionStoreDocument> LoadDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(SessionPath))
        {
            return new SessionStoreDocument();
        }

        var json = await File.ReadAllTextAsync(SessionPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SessionStoreDocument();
        }

        JsonNode? rootNode;
        try
        {
            rootNode = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            rootNode = null;
        }

        if (rootNode is JsonObject rootObject && rootObject["sessions"] is not null)
        {
            var document = rootNode.Deserialize<SessionStoreDocument>(SerializerOptions);
            if (document is not null)
            {
                return document.WithOrdinalSessions();
            }
        }

        var legacySession = JsonSerializer.Deserialize<SessionRecord>(json, SerializerOptions);
        return legacySession is null
            ? new SessionStoreDocument()
            : new SessionStoreDocument
            {
                Sessions = new Dictionary<string, SessionRecord>(StringComparer.Ordinal)
                {
                    [DefaultSessionKey] = legacySession
                }
            };
    }

    private static string ResolveDefaultSessionPath()
    {
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            homeDirectory = Environment.GetEnvironmentVariable("HOME");
        }

        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            throw new InvalidOperationException("Unable to resolve the user home directory for Halley session storage.");
        }

        return Path.Combine(homeDirectory, ".halley", "session.json");
    }

    private sealed class SessionStoreDocument
    {
        public Dictionary<string, SessionRecord> Sessions { get; init; } = new(StringComparer.Ordinal);

        public SessionStoreDocument WithOrdinalSessions() =>
            new()
            {
                Sessions = new Dictionary<string, SessionRecord>(Sessions, StringComparer.Ordinal)
            };
    }
}
