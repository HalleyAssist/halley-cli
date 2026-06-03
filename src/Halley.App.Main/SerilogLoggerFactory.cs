using Serilog;
using Serilog.Core;

namespace Halley.App.Main;

public sealed class SerilogLoggerFactory : IHalleyLoggerFactory
{
    public Logger Create(HalleyLogConfiguration configuration, TextWriter output, bool interactive)
    {
        var loggerConfiguration = new LoggerConfiguration().Enrich.FromLogContext();

        if (!configuration.IsEnabled)
        {
            return loggerConfiguration.CreateLogger();
        }

        return loggerConfiguration
            .MinimumLevel.Is(configuration.MinimumLevel)
            .WriteTo.Sink(new TextWriterLogSink(output, interactive))
            .CreateLogger();
    }
}
