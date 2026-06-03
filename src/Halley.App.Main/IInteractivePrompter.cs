namespace Halley.App.Main;

public sealed record InteractiveSuggestion(string Value, string? Description = null);

public interface IInteractivePrompter
{
    bool IsInteractive { get; }

    Task<string?> ReadLineAsync(
        TextWriter output,
        string prompt,
        IReadOnlyList<InteractiveSuggestion>? suggestions = null,
        string? helpText = null,
        CancellationToken cancellationToken = default);

    Task<string?> ReadMultilineAsync(
        TextWriter output,
        string prompt,
        IReadOnlyList<InteractiveSuggestion>? suggestions = null,
        string? helpText = null,
        CancellationToken cancellationToken = default);
}
