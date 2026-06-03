namespace Halley.App.Main;

public interface IPasswordPrompt
{
    Task<string?> ReadPasswordAsync(TextWriter output, CancellationToken cancellationToken = default);
}
