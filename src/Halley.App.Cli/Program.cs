using Halley.App.Main;
using System.CommandLine;

var rootCommand = new RootCommand("The Halley Utility");
var versionCommand = new Command("version", "Print version and git SHA");

versionCommand.SetAction(_ =>
{
    Console.WriteLine($"Version: {BuildInfo.Version}");
    Console.WriteLine($"Git SHA: {BuildInfo.GitSha}");
});

rootCommand.Add(versionCommand);
return await rootCommand.Parse(args).InvokeAsync();
