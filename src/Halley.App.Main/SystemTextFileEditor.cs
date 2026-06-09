using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Halley.App.Main;

public sealed class SystemTextFileEditor : ITextFileEditor
{
    public void Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (OperatingSystem.IsWindows())
        {
            StartProcess("notepad.exe", [path]);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            StartProcess("open", ["-t", path]);
            return;
        }

        var preferredEditor = Environment.GetEnvironmentVariable("VISUAL");
        if (string.IsNullOrWhiteSpace(preferredEditor))
        {
            preferredEditor = Environment.GetEnvironmentVariable("EDITOR");
        }

        if (!string.IsNullOrWhiteSpace(preferredEditor))
        {
            StartShellCommand($"{preferredEditor} {EscapeShellArgument(path)}");
            return;
        }

        foreach (var editor in new[] { "sensible-editor", "editor", "xdg-open", "nano", "vi", "vim" })
        {
            try
            {
                StartProcess(editor, [path]);
                return;
            }
            catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
            {
            }
        }

        throw new InvalidOperationException("Unable to find a system text editor. Set VISUAL or EDITOR to your preferred editor.");
    }

    private static void StartShellCommand(string command)
    {
        var fileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        var args = OperatingSystem.IsWindows()
            ? new[] { "/c", command }
            : new[] { "-lc", command };

        StartProcess(fileName, args);
    }

    private static void StartProcess(string fileName, IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException($"Failed to start `{fileName}`.");
        }
    }

    private static string EscapeShellArgument(string value)
    {
        var builder = new StringBuilder();
        builder.Append('\'');
        foreach (var ch in value)
        {
            if (ch == '\'')
            {
                builder.Append("'\"'\"'");
            }
            else
            {
                builder.Append(ch);
            }
        }

        builder.Append('\'');
        return builder.ToString();
    }
}
