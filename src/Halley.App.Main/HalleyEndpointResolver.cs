using Halley.App.Api;

namespace Halley.App.Main;

public static class HalleyEndpointResolver
{
    public static HalleyApiClientOptions Resolve(string? input)
    {
        if (HalleyApiClientOptions.TryCreate(input, out var options, out var error))
        {
            return options!;
        }

        throw new ArgumentException(error, nameof(input));
    }

    public static Uri NormalizeRootUri(string? input) => Resolve(input).AuthBaseUri;
}
