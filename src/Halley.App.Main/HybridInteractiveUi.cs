namespace Halley.App.Main;

public sealed class HybridInteractiveUi : IInteractiveUi
{
    private readonly IInteractiveUi _richUi;
    private readonly IInteractiveUi _fallbackUi;

    public HybridInteractiveUi(IInteractiveUi richUi, IInteractiveUi fallbackUi)
    {
        _richUi = richUi;
        _fallbackUi = fallbackUi;
    }

    public bool IsInteractive => _richUi.IsInteractive || _fallbackUi.IsInteractive;

    public bool SupportsCallCreateWizard => _richUi.IsInteractive && _richUi.SupportsCallCreateWizard;

    public async Task<string?> ReadPasswordAsync(TextWriter output, CancellationToken cancellationToken = default)
    {
        if (_richUi.IsInteractive)
        {
            try
            {
                return await _richUi.ReadPasswordAsync(output, cancellationToken);
            }
            catch
            {
                // Fall back to the plain console path if the rich UI cannot start.
            }
        }

        return await _fallbackUi.ReadPasswordAsync(output, cancellationToken);
    }

    public Task<string?> ReadLineAsync(
        TextWriter output,
        string prompt,
        IReadOnlyList<InteractiveSuggestion>? suggestions = null,
        string? helpText = null,
        CancellationToken cancellationToken = default) =>
        _fallbackUi.ReadLineAsync(output, prompt, suggestions, helpText, cancellationToken);

    public Task<string?> ReadMultilineAsync(
        TextWriter output,
        string prompt,
        IReadOnlyList<InteractiveSuggestion>? suggestions = null,
        string? helpText = null,
        CancellationToken cancellationToken = default) =>
        _fallbackUi.ReadMultilineAsync(output, prompt, suggestions, helpText, cancellationToken);

    public async Task<InteractiveCallCreateResult> RunCallCreateWizardAsync(
        InteractiveCallCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_richUi.IsInteractive && _richUi.SupportsCallCreateWizard)
        {
            try
            {
                return await _richUi.RunCallCreateWizardAsync(request, cancellationToken);
            }
            catch
            {
                // Fall back to the plain console path handled by HalleyCliApplication.
            }
        }

        throw new NotSupportedException("The current interactive UI does not support the structured call-create wizard.");
    }
}
