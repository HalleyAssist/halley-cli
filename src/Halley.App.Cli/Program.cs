using Halley.App.Api;
using Halley.App.Main;

using var httpClient = new HttpClient();
var sessionStore = new FileSessionStore();
var replayCommandName = Path.GetFileName(Environment.ProcessPath) ?? "halley-cli";
var application = new HalleyCliApplication(
    (options, logger) => new HalleyApiClient(httpClient, options, logger),
    sessionStore,
    Console.Out,
    Console.Error,
    replayCommandNameProvider: () => replayCommandName);
return await application.RunAsync(args);
