using System.Text.Json;

namespace Halley.App.Api;

public sealed record SessionRecord(string Token, string AuthType, DateTimeOffset SavedAtUtc);

public interface ISessionStore
{
    string SessionPath { get; }

    Task<SessionRecord?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(SessionRecord session, CancellationToken cancellationToken = default);
}

public sealed class FileSessionStore(string? sessionPath = null) : ISessionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string SessionPath { get; } = sessionPath ?? ResolveDefaultSessionPath();

    public async Task<SessionRecord?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SessionPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(SessionPath);
        return await JsonSerializer.DeserializeAsync<SessionRecord>(stream, SerializerOptions, cancellationToken);
    }

    public async Task SaveAsync(SessionRecord session, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(SessionPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(session, SerializerOptions);
        await File.WriteAllTextAsync(SessionPath, json, cancellationToken);
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
}
