using System.Text;

namespace Halley.App.Main;

public sealed class ConsolePasswordPrompt : IPasswordPrompt
{
    public async Task<string?> ReadPasswordAsync(TextWriter output, CancellationToken cancellationToken = default)
    {
        await output.WriteAsync("Password: ".AsMemory(), cancellationToken);
        await output.FlushAsync();

        if (Console.IsInputRedirected)
        {
            var redirectedPassword = await Console.In.ReadLineAsync(cancellationToken);
            await output.WriteLineAsync();
            await output.FlushAsync();
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

        await output.WriteLineAsync();
        await output.FlushAsync();
        return password.ToString();
    }
}
