using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Halley.App.Main;

internal sealed class TextWriterLogSink(TextWriter output, bool interactive) : ILogEventSink
{
    private const string Reset = "\u001b[0m";

    private static readonly MessageTemplateTextFormatter Formatter =
        new("[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}", null);

    private readonly object _sync = new();

    public void Emit(LogEvent logEvent)
    {
        lock (_sync)
        {
            if (interactive)
            {
                output.Write(GetColor(logEvent.Level));
            }

            Formatter.Format(logEvent, output);

            if (interactive)
            {
                output.Write(Reset);
            }

            output.Flush();
        }
    }

    private static string GetColor(LogEventLevel level) =>
        level switch
        {
            LogEventLevel.Verbose => "\u001b[90m",
            LogEventLevel.Debug => "\u001b[37m",
            LogEventLevel.Information => "\u001b[36m",
            LogEventLevel.Warning => "\u001b[33m",
            LogEventLevel.Error => "\u001b[31m",
            LogEventLevel.Fatal => "\u001b[91m",
            _ => string.Empty
        };
}
