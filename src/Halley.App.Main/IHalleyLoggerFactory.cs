using Serilog.Core;

namespace Halley.App.Main;

public interface IHalleyLoggerFactory
{
    Logger Create(HalleyLogConfiguration configuration, TextWriter output, bool interactive);
}
