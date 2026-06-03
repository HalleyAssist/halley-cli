using System.Reflection;

namespace Halley.App.Main;

public static class BuildInfo
{
    private static readonly Assembly Assembly = typeof(BuildInfo).Assembly;

    private static readonly string InformationalVersion =
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.1.0-dev+local";

    private static readonly string[] Parts = InformationalVersion.Split('+', 2);

    public static string Version => Parts[0];

    public static string GitSha => GetGitSha();

    private static string GetGitSha()
    {
        foreach (var metadata in Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (metadata.Key == "GitSha" && !string.IsNullOrWhiteSpace(metadata.Value))
            {
                return metadata.Value;
            }
        }

        return Parts.Length > 1 ? Parts[1] : "local";
    }
}
