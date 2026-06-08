namespace Halley.App.Main;

public sealed record InteractiveSuggestion(string Value, string? Description = null);

public sealed record InteractiveCallCreateQuestion(int Id, string Text, string Format);

public sealed record InteractiveCallCreateRequest(
    IReadOnlyList<InteractiveSuggestion> Organisations,
    IReadOnlyList<InteractiveSuggestion> Timezones,
    Func<string, CancellationToken, Task<IReadOnlyList<InteractiveSuggestion>>> LoadTemplateSuggestionsAsync,
    Func<string, string, CancellationToken, Task<IReadOnlyList<InteractiveSuggestion>>> LoadTemplateVersionSuggestionsAsync);

public sealed record InteractiveCallCreateResult(
    bool Cancelled,
    string? OrganisationReference,
    string? CallMethod,
    string? PhoneNumber,
    string? RecipientName,
    string? RecipientTimezone,
    string? TemplateReference,
    int? TemplateId,
    string? Instructions,
    string? Agenda,
    IReadOnlyList<string> Notes,
    IReadOnlyList<InteractiveCallCreateQuestion> Questions)
{
    public static InteractiveCallCreateResult CancelledResult() =>
        new(
            true,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            []);
}

public interface IInteractiveUi
{
    bool IsInteractive { get; }

    bool SupportsCallCreateWizard { get; }

    Task<string?> ReadPasswordAsync(TextWriter output, CancellationToken cancellationToken = default);

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

    Task<InteractiveCallCreateResult> RunCallCreateWizardAsync(
        InteractiveCallCreateRequest request,
        CancellationToken cancellationToken = default);
}
