using System.Text;

namespace Halley.App.Main;

public sealed class ConsoleInteractiveUi : IInteractiveUi
{
    public bool IsInteractive => !Console.IsInputRedirected && !Console.IsErrorRedirected;

    public bool SupportsCallCreateWizard => false;

    public async Task<string?> ReadPasswordAsync(TextWriter output, CancellationToken cancellationToken = default)
    {
        await output.WriteAsync("Password: ".AsMemory(), cancellationToken);
        await output.FlushAsync(cancellationToken);

        if (Console.IsInputRedirected)
        {
            var redirectedPassword = await Console.In.ReadLineAsync(cancellationToken);
            await output.WriteLineAsync(string.Empty.AsMemory(), cancellationToken);
            await output.FlushAsync(cancellationToken);
            return redirectedPassword;
        }

        var password = new StringBuilder();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
            }
        }

        await output.WriteLineAsync(string.Empty.AsMemory(), cancellationToken);
        await output.FlushAsync(cancellationToken);
        return password.ToString();
    }

    public async Task<string?> ReadLineAsync(
        TextWriter output,
        string prompt,
        IReadOnlyList<InteractiveSuggestion>? suggestions = null,
        string? helpText = null,
        CancellationToken cancellationToken = default)
    {
        await WritePromptHeaderAsync(output, suggestions, helpText, cancellationToken);

        if (IsInteractive && ReferenceEquals(output, Console.Error) && suggestions is { Count: > 0 })
        {
            return await ReadLineWithAutocompleteAsync(output, prompt, suggestions, cancellationToken);
        }

        await output.WriteAsync(prompt.AsMemory(), cancellationToken);
        await output.FlushAsync(cancellationToken);
        return await Console.In.ReadLineAsync(cancellationToken);
    }

    public async Task<string?> ReadMultilineAsync(
        TextWriter output,
        string prompt,
        IReadOnlyList<InteractiveSuggestion>? suggestions = null,
        string? helpText = null,
        CancellationToken cancellationToken = default)
    {
        await WritePromptHeaderAsync(output, suggestions, helpText, cancellationToken);
        await output.WriteLineAsync(prompt.AsMemory(), cancellationToken);
        await output.WriteLineAsync("(Finish with an empty line.)".AsMemory(), cancellationToken);
        await output.FlushAsync(cancellationToken);

        var lines = new List<string>();
        while (true)
        {
            var line = await Console.In.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
            }

            if (line.Length == 0)
            {
                return string.Join(Environment.NewLine, lines);
            }

            lines.Add(line);
        }
    }

    public Task<InteractiveCallCreateResult> RunCallCreateWizardAsync(
        InteractiveCallCreateRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The plain console UI does not support the structured call-create wizard.");

    private static async Task WritePromptHeaderAsync(
        TextWriter output,
        IReadOnlyList<InteractiveSuggestion>? suggestions,
        string? helpText,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(helpText))
        {
            await output.WriteLineAsync(helpText.AsMemory(), cancellationToken);
        }

        if (suggestions is not { Count: > 0 })
        {
            return;
        }

        foreach (var suggestion in suggestions.Take(8))
        {
            var line = string.IsNullOrWhiteSpace(suggestion.Description)
                ? $"  {suggestion.Value}"
                : $"  {suggestion.Value} - {suggestion.Description}";
            await output.WriteLineAsync(line.AsMemory(), cancellationToken);
        }

        if (suggestions.Count > 8)
        {
            await output.WriteLineAsync("  ...".AsMemory(), cancellationToken);
        }
    }

    private static async Task<string?> ReadLineWithAutocompleteAsync(
        TextWriter output,
        string prompt,
        IReadOnlyList<InteractiveSuggestion> suggestions,
        CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();
        var cycleMatches = Array.Empty<InteractiveSuggestion>();
        var cyclePrefix = string.Empty;
        var cycleIndex = -1;
        var renderedLength = 0;

        await output.WriteAsync(prompt.AsMemory(), cancellationToken);
        await output.FlushAsync(cancellationToken);
        renderedLength = prompt.Length;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                await output.WriteLineAsync(string.Empty.AsMemory(), cancellationToken);
                await output.FlushAsync(cancellationToken);
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                    ResetAutocompleteState(ref cycleMatches, ref cyclePrefix, ref cycleIndex);
                    renderedLength = await RewritePromptAsync(output, prompt, buffer.ToString(), renderedLength, cancellationToken);
                }

                continue;
            }

            if (key.Key == ConsoleKey.Tab)
            {
                var currentText = buffer.ToString();
                if (!string.Equals(cyclePrefix, currentText, StringComparison.Ordinal))
                {
                    cyclePrefix = currentText;
                    cycleMatches = InteractiveSuggestionMatcher.GetMatches(currentText, suggestions).ToArray();
                    cycleIndex = -1;
                }

                if (cycleMatches.Length == 0)
                {
                    continue;
                }

                cycleIndex = (cycleIndex + 1) % cycleMatches.Length;
                buffer.Clear();
                buffer.Append(cycleMatches[cycleIndex].Value);
                renderedLength = await RewritePromptAsync(output, prompt, buffer.ToString(), renderedLength, cancellationToken);
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
                ResetAutocompleteState(ref cycleMatches, ref cyclePrefix, ref cycleIndex);
                await output.WriteAsync(new string(key.KeyChar, 1).AsMemory(), cancellationToken);
                await output.FlushAsync(cancellationToken);
                renderedLength += 1;
            }
        }
    }

    private static void ResetAutocompleteState(
        ref InteractiveSuggestion[] cycleMatches,
        ref string cyclePrefix,
        ref int cycleIndex)
    {
        cycleMatches = [];
        cyclePrefix = string.Empty;
        cycleIndex = -1;
    }

    private static async Task<int> RewritePromptAsync(
        TextWriter output,
        string prompt,
        string value,
        int previousLength,
        CancellationToken cancellationToken)
    {
        var rendered = prompt + value;
        var clearWidth = Math.Max(previousLength, rendered.Length);
        var clearText = "\r" + new string(' ', clearWidth) + "\r" + rendered;
        await output.WriteAsync(clearText.AsMemory(), cancellationToken);
        await output.FlushAsync(cancellationToken);
        return rendered.Length;
    }
}
