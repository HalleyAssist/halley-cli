using Halley.App.Api;
using Halley.App.Main;

using var httpClient = new HttpClient();
var sessionStore = new FileSessionStore();
var application = new HalleyCliApplication(
    (options, logger) => new HalleyApiClient(httpClient, options, logger),
    sessionStore,
    Console.Out,
    Console.Error);
return await application.RunAsync(args);
