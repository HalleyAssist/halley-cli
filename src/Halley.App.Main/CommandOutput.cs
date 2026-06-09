using System.Text.Json.Nodes;

namespace Halley.App.Main;

public enum CommandOutputKind
{
    JsonPayload,
    Token,
    Version,
    Empty
}

public sealed record CommandOutput
{
    public required CommandOutputKind Kind { get; init; }

    public JsonNode? JsonPayload { get; init; }

    public JsonNode? HumanPayload { get; init; }

    public string? Token { get; init; }

    public string? Version { get; init; }

    public string? GitSha { get; init; }

    public static CommandOutput Empty() => new() { Kind = CommandOutputKind.Empty };

    public static CommandOutput Json(JsonNode payload) => new() { Kind = CommandOutputKind.JsonPayload, JsonPayload = payload, HumanPayload = payload };

    public static CommandOutput Json(JsonNode jsonPayload, JsonNode humanPayload) => new()
    {
        Kind = CommandOutputKind.JsonPayload,
        JsonPayload = jsonPayload,
        HumanPayload = humanPayload
    };

    public static CommandOutput TokenValue(string token) => new() { Kind = CommandOutputKind.Token, Token = token };

    public static CommandOutput VersionValue(string version, string gitSha) => new()
    {
        Kind = CommandOutputKind.Version,
        Version = version,
        GitSha = gitSha
    };
}
