using Serilog.Events;

namespace Halley.App.Main;

public sealed record HalleyLogConfiguration(bool IsEnabled, LogEventLevel MinimumLevel)
{
    public static HalleyLogConfiguration Disabled() => new(false, LogEventLevel.Fatal);

    public static HalleyLogConfiguration Enabled(LogEventLevel minimumLevel) => new(true, minimumLevel);
}
