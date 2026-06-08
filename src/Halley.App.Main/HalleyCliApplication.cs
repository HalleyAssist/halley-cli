using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Halley.App.Api;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Halley.App.Main;

public sealed class HalleyCliApplication
{
    private static readonly Logger SilentLogger = new LoggerConfiguration().CreateLogger();
    private static readonly IReadOnlyList<InteractiveSuggestion> CallModeSuggestions =
    [
        new("template", "Use a call template."),
        new("manual", "Write the call content yourself."),
        new("template+manual", "Start from a template and override details.")
    ];
    private static readonly IReadOnlyList<InteractiveSuggestion> CallMethodSuggestions =
    [
        new("phone", "Place a phone call."),
        new("web", "Use a web-based call flow.")
    ];
    private static readonly IReadOnlyList<InteractiveSuggestion> YesNoSuggestions =
    [
        new("yes", "Answer yes."),
        new("no", "Answer no.")
    ];
    private static readonly IReadOnlyList<InteractiveSuggestion> PhoneNumberSuggestions =
    [
        new("+61400000000", "Example mobile number."),
        new("+441234567890", "Example international number.")
    ];
    private static readonly IReadOnlyList<InteractiveSuggestion> RecipientNameSuggestions =
    [
        new("Test User", "A placeholder recipient."),
        new("Pat Example", "Another example recipient.")
    ];
    private static readonly IReadOnlyList<InteractiveSuggestion> InstructionsSuggestions =
    [
        new("Confirm the recipient's wellbeing.", "A short instruction starter."),
        new("Ask concise questions and record clear answers.", "A style prompt.")
    ];
    private static readonly IReadOnlyList<InteractiveSuggestion> AgendaSuggestions =
    [
        new("Introduce yourself", "A simple first agenda line."),
        new("Confirm the recipient is safe", "A useful wellbeing check."),
        new("Summarize any follow-up actions", "A typical closing step.")
    ];
    private static readonly IReadOnlyList<InteractiveSuggestion> NoteSuggestions =
    [
        new("Weather was warm", "Example call context."),
        new("Resident prefers afternoon calls", "Example preference note.")
    ];
    private static readonly IReadOnlyList<InteractiveSuggestion> QuestionIdSuggestions =
    [
        new("1", "First result question."),
        new("2", "Second result question."),
        new("3", "Third result question.")
    ];
    private static readonly IReadOnlyList<InteractiveSuggestion> QuestionFormatSuggestions =
    [
        new("string", "Free text response."),
        new("boolean", "Yes or no response.")
    ];
    private static readonly IReadOnlyList<InteractiveSuggestion> QuestionTextSuggestions =
    [
        new("Was the resident okay?", "Example boolean question."),
        new("What follow-up is needed?", "Example text question.")
    ];

    private readonly Func<HalleyApiClientOptions, ILogger, IHalleyApiClient> _apiClientFactory;
    private readonly ISessionStore _sessionStore;
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly IInteractiveUi _interactiveUi;
    private readonly ITextFileEditor _textFileEditor;
    private readonly IAsyncClock _clock;
    private readonly IHalleyLoggerFactory _loggerFactory;
    private readonly Func<string> _replayCommandNameProvider;
    private readonly Func<bool> _isWindowsProvider;
    private readonly HalleyOutputFormatter _formatter = new();
    private readonly AsyncLocal<ILogger?> _currentLogger = new();
    private readonly Option<string> _outputOption;
    private readonly Option<string?> _tokenOption;
    private readonly Option<string> _endpointOption;
    private readonly Option<string?> _logOption;
    private readonly RootCommand _rootCommand;

    public HalleyCliApplication(
        Func<HalleyApiClientOptions, ILogger, IHalleyApiClient> apiClientFactory,
        ISessionStore sessionStore,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        IInteractiveUi? interactiveUi = null,
        ITextFileEditor? textFileEditor = null,
        IAsyncClock? clock = null,
        IHalleyLoggerFactory? loggerFactory = null,
        Func<string>? replayCommandNameProvider = null,
        Func<bool>? isWindowsProvider = null)
    {
        _apiClientFactory = apiClientFactory;
        _sessionStore = sessionStore;
        _stdout = stdout ?? Console.Out;
        _stderr = stderr ?? Console.Error;
        _interactiveUi = interactiveUi ?? new HybridInteractiveUi(new ConsoloniaInteractiveUi(), new ConsoleInteractiveUi());
        _textFileEditor = textFileEditor ?? new SystemTextFileEditor();
        _clock = clock ?? new SystemAsyncClock();
        _loggerFactory = loggerFactory ?? new SerilogLoggerFactory();
        _replayCommandNameProvider = replayCommandNameProvider ?? DefaultReplayCommandNameProvider;
        _isWindowsProvider = isWindowsProvider ?? OperatingSystem.IsWindows;
        _outputOption = CreateOutputOption();
        _tokenOption = CreateOption<string?>("--token", "JWT token to use instead of the saved session token.");
        _tokenOption.Recursive = true;
        _endpointOption = CreateEndpointOption();
        _logOption = CreateLogOption();
        _rootCommand = BuildRootCommand();
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var parseResult = _rootCommand.Parse(args);
        var outputMode = GetOutputMode(parseResult);
        if (!TryResolveLogConfiguration(args, parseResult, out var logConfiguration, out var logError))
        {
            return await WriteCliErrorAsync(logError!, outputMode, cancellationToken);
        }

        using var logger = _loggerFactory.Create(logConfiguration, _stderr, IsInteractiveConsole());
        _currentLogger.Value = logger;

        var invocationConfiguration = new InvocationConfiguration
        {
            Output = _stdout,
            Error = _stderr
        };

        try
        {
            CurrentLogger.Debug("CLI invocation started. Arguments: {@Arguments}", SanitizeArguments(args));
            var exitCode = await parseResult.InvokeAsync(invocationConfiguration, cancellationToken);
            CurrentLogger.Debug("CLI invocation completed with exit code {ExitCode}.", exitCode);
            return exitCode;
        }
        finally
        {
            _currentLogger.Value = null;
        }
    }

    private static Option<string> CreateOutputOption()
    {
        var option = CreateOption<string>("--output", "Output format: human or json.");
        option.Recursive = true;
        option.DefaultValueFactory = _ => "human";
        option.AcceptOnlyFromAmong("human", "json");
        return option;
    }

    private static Option<string> CreateEndpointOption()
    {
        var option = CreateOption<string>("--endpoint", "Halley cloud endpoint, for example `halleyassist.com`, `cloud.halleyassist.com`, or a full URL.");
        option.Recursive = true;
        option.DefaultValueFactory = _ => "https://cloud.halleyassist.com";
        return option;
    }

    private static Option<string?> CreateLogOption()
    {
        var option = CreateOption<string?>("--log", "Global log level. Defaults to `warning`; use `--log` alone for `info`, or set `trace`, `debug`, `info`, `warning`, `error`, `fatal`, or `none`.");
        option.Recursive = true;
        option.Arity = ArgumentArity.ZeroOrOne;
        return option;
    }

    private RootCommand BuildRootCommand()
    {
        var rootCommand = new RootCommand("The Halley Utility");
        rootCommand.Add(_outputOption);
        rootCommand.Add(_tokenOption);
        rootCommand.Add(_endpointOption);
        rootCommand.Add(_logOption);
        rootCommand.Add(CreateVersionCommand());
        rootCommand.Add(CreateLoginCommand());
        rootCommand.Add(CreateCallsCommand());
        rootCommand.Add(CreateApiKeysCommand());
        rootCommand.Add(CreateOrganisationsCommand());
        rootCommand.Add(CreateUsersCommand());
        return rootCommand;
    }

    private Command CreateVersionCommand()
    {
        var command = new Command("version", "Print version and git SHA.");
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputMode = GetOutputMode(parseResult);
            return await WriteSuccessAsync(CommandOutput.VersionValue(BuildInfo.Version, BuildInfo.GitSha), outputMode, cancellationToken);
        });
        return command;
    }

    private Command CreateLoginCommand()
    {
        var command = new Command("login", "Authenticate and store a session token.");
        command.Add(CreateUserLoginCommand());
        command.Add(CreateApiKeyLoginCommand());
        command.Add(CreateLoginTokensCommand());
        command.Add(CreateLoginEditCommand());
        return command;
    }

    private Command CreateUserLoginCommand()
    {
        var usernameOption = CreateRequiredOption<string>("--username", "The user name to authenticate.");
        var passwordOption = CreateOption<string?>("--password", "The password to authenticate. If omitted, the CLI prompts for it.");

        var command = new Command("user", "Authenticate with a user name and password.");
        command.Add(usernameOption);
        command.Add(passwordOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputMode = GetOutputMode(parseResult);
            var apiClient = GetApiClient(parseResult);
            if (apiClient.IsError)
            {
                return await WriteCliErrorAsync(apiClient.ErrorMessage!, outputMode, cancellationToken);
            }

            var password = parseResult.GetValue(passwordOption);
            if (string.IsNullOrWhiteSpace(password))
            {
                CurrentLogger.Debug("Prompting for a password for user {UserName}.", parseResult.GetRequiredValue(usernameOption));
                password = await _interactiveUi.ReadPasswordAsync(_stderr, cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                return await WriteCliErrorAsync("A password is required.", outputMode, cancellationToken);
            }

            var request = new UserLoginRequest(
                parseResult.GetRequiredValue(usernameOption),
                password);

            var result = await apiClient.Value!.LoginUserAsync(request, cancellationToken);
            return await HandleLoginResultAsync(result, "user", parseResult, outputMode, cancellationToken);
        });

        return command;
    }

    private Command CreateApiKeyLoginCommand()
    {
        var secretOption = CreateRequiredOption<string>("--secret", "The API key secret to exchange for a JWT.");

        var command = new Command("api-key", "Authenticate with an API key secret.");
        command.Add(secretOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputMode = GetOutputMode(parseResult);
            var apiClient = GetApiClient(parseResult);
            if (apiClient.IsError)
            {
                return await WriteCliErrorAsync(apiClient.ErrorMessage!, outputMode, cancellationToken);
            }

            var request = new ApiKeyLoginRequest(parseResult.GetRequiredValue(secretOption));
            var result = await apiClient.Value!.LoginApiKeyAsync(request, cancellationToken);
            return await HandleLoginResultAsync(result, "api-key", parseResult, outputMode, cancellationToken);
        });

        return command;
    }

    private Command CreateLoginTokensCommand()
    {
        var command = new Command("tokens", "List all locally saved session tokens.");
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputMode = GetOutputMode(parseResult);

            try
            {
                var sessions = await _sessionStore.LoadAllAsync(cancellationToken);
                var tokenObjects = sessions
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry =>
                    {
                        var expiresAtUtc = JwtTokenInspector.TryGetExpirationUtc(entry.Value.Token, out var exp) ? exp : null;
                        return new JsonObject
                        {
                            ["endpoint"] = entry.Key,
                            ["auth_type"] = entry.Value.AuthType,
                            ["saved_at"] = entry.Value.SavedAt.ToString("o", CultureInfo.InvariantCulture),
                            ["expires_at_utc"] = expiresAtUtc?.UtcDateTime.ToString("u", CultureInfo.InvariantCulture),
                            ["expired"] = expiresAtUtc is not null && expiresAtUtc.Value <= _clock.UtcNow,
                            ["token"] = entry.Value.Token
                        };
                    })
                    .ToArray();

                var jsonArray = new JsonArray(tokenObjects.Select(token => (JsonNode)token).ToArray());
                var humanArray = new JsonArray(tokenObjects.Select(token => token.DeepClone()).ToArray());

                return await WriteSuccessAsync(
                    CommandOutput.Json(
                        new JsonObject { ["tokens"] = jsonArray },
                        humanArray),
                    outputMode,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                CurrentLogger.Error(ex, "Failed to read session tokens from {SessionPath}.", _sessionStore.SessionPath);
                return await WriteCliErrorAsync($"Failed to read session tokens from {_sessionStore.SessionPath}: {ex.Message}", outputMode, cancellationToken);
            }
        });

        return command;
    }

    private Command CreateLoginEditCommand()
    {
        var command = new Command("edit", "Open the local session file in the system text editor.");
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputMode = GetOutputMode(parseResult);

            try
            {
                await _sessionStore.EnsureExistsAsync(cancellationToken);
                _textFileEditor.Open(_sessionStore.SessionPath);

                return await WriteSuccessAsync(
                    CommandOutput.Json(
                        new JsonObject
                        {
                            ["opened"] = true,
                            ["path"] = _sessionStore.SessionPath
                        },
                        new JsonObject
                        {
                            ["status"] = "Opened local auth file.",
                            ["path"] = _sessionStore.SessionPath
                        }),
                    outputMode,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                CurrentLogger.Error(ex, "Failed to open session file {SessionPath}.", _sessionStore.SessionPath);
                return await WriteCliErrorAsync($"Failed to open session file {_sessionStore.SessionPath}: {ex.Message}", outputMode, cancellationToken);
            }
        });

        return command;
    }

    private Command CreateCallsCommand()
    {
        var command = new Command("calls", "Create and inspect call requests and results.");
        command.Add(CreateCallsCreateCommand());
        command.Add(CreateCallsStatusCommand());
        command.Add(CreateCallsResultsCommand());
        return command;
    }

    private Command CreateCallsCreateCommand()
    {
        var options = CreateCallCreateOptions();
        var executionOptions = CreateCallExecutionOptions();

        var command = new Command("create", "Create a new call request.");
        AddCallCreateOptions(command, options);
        AddCallExecutionOptions(command, executionOptions);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputMode = GetOutputMode(parseResult);
            var apiClient = GetApiClient(parseResult);
            if (apiClient.IsError)
            {
                return await WriteCliErrorAsync(apiClient.ErrorMessage!, outputMode, cancellationToken);
            }

            var token = await RequireTokenAsync(parseResult, outputMode, cancellationToken);
            if (token is null)
            {
                return 1;
            }

            var input = await ResolveCallCreateInputAsync(apiClient.Value!, token, parseResult, options, outputMode, cancellationToken);
            if (input.ErrorResult is not null)
            {
                return await WriteApiErrorAsync(input.ErrorResult, outputMode, cancellationToken);
            }

            if (input.ErrorMessage is not null)
            {
                return await WriteCliErrorAsync(input.ErrorMessage, outputMode, cancellationToken);
            }

            if (input.ShouldReturnCommandOnly)
            {
                return await WriteSuccessAsync(BuildCallCommandOutput(parseResult, input.Input!), outputMode, cancellationToken);
            }

            var createResult = await apiClient.Value!.CreateCallRequestAsync(token, BuildCallRequest(input.Input!), cancellationToken);
            var shouldWait = parseResult.GetValue(executionOptions.WaitOption);
            var shouldDelete = parseResult.GetValue(executionOptions.DeleteOption);

            if (!shouldWait && !shouldDelete)
            {
                var createExitCode = await HandleApiResultAsync(createResult, outputMode, cancellationToken);
                if (createExitCode == 0 && input.ShouldWriteReplayHint)
                {
                    await WriteCallReplayHintAsync(parseResult, input.Input!, cancellationToken);
                }

                return createExitCode;
            }

            if (!createResult.IsSuccessStatusCode)
            {
                return await WriteApiErrorAsync(createResult, outputMode, cancellationToken);
            }

            var callRequestUuid = createResult.JsonBody?["call_request"]?["uuid"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(callRequestUuid))
            {
                return await WriteCliErrorAsync("The call creation response did not contain a call request uuid.", outputMode, cancellationToken);
            }

            var waitConfiguration = GetCallWaitConfiguration(parseResult, executionOptions);
            if (waitConfiguration.ErrorMessage is not null)
            {
                return await WriteCliErrorAsync(waitConfiguration.ErrorMessage, outputMode, cancellationToken);
            }

            var status = await LoadCallStatusAsync(
                apiClient.Value!,
                token,
                callRequestUuid,
                waitForResult: shouldWait,
                waitConfiguration.Value!,
                cancellationToken);

            if (status.ErrorResult is not null)
            {
                return await WriteApiErrorAsync(status.ErrorResult, outputMode, cancellationToken);
            }

            if (status.ErrorMessage is not null)
            {
                return await WriteCliErrorAsync(status.ErrorMessage, outputMode, cancellationToken);
            }

            var exitCode = status.TimedOut ? 1 : 0;
            if (shouldDelete)
            {
                var deleteResult = await DeleteLatestCallResultIfRequestedAsync(apiClient.Value!, token, callRequestUuid, status, outputMode, cancellationToken);
                if (deleteResult.ErrorResult is not null)
                {
                    return await WriteApiErrorAsync(deleteResult.ErrorResult, outputMode, cancellationToken);
                }

                if (deleteResult.ErrorMessage is not null)
                {
                    return await WriteCliErrorAsync(deleteResult.ErrorMessage, outputMode, cancellationToken);
                }

                if (deleteResult.WarningMessage is not null)
                {
                    await WriteCliWarningAsync(deleteResult.WarningMessage, outputMode, cancellationToken);
                }

                status = ApplyDeleteOutcome(status, deleteResult);
            }

            await WriteSuccessAsync(CommandOutput.Json(status.JsonPayload!, status.HumanPayload!), outputMode, cancellationToken);
            if (input.ShouldWriteReplayHint)
            {
                await WriteCallReplayHintAsync(parseResult, input.Input!, cancellationToken);
            }

            return exitCode;
        });

        return command;
    }

    private Command CreateCallsStatusCommand()
    {
        var callRequestUuidArgument = new Argument<string>("call-request-uuid") { Description = "The call request uuid." };
        var executionOptions = CreateCallExecutionOptions();

        var command = new Command("status", "Show the current state of a call request.");
        command.Add(callRequestUuidArgument);
        AddCallExecutionOptions(command, executionOptions);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputMode = GetOutputMode(parseResult);
            var apiClient = GetApiClient(parseResult);
            if (apiClient.IsError)
            {
                return await WriteCliErrorAsync(apiClient.ErrorMessage!, outputMode, cancellationToken);
            }

            var token = await RequireTokenAsync(parseResult, outputMode, cancellationToken);
            if (token is null)
            {
                return 1;
            }

            var waitConfiguration = GetCallWaitConfiguration(parseResult, executionOptions);
            if (waitConfiguration.ErrorMessage is not null)
            {
                return await WriteCliErrorAsync(waitConfiguration.ErrorMessage, outputMode, cancellationToken);
            }

            var status = await LoadCallStatusAsync(
                apiClient.Value!,
                token,
                parseResult.GetRequiredValue(callRequestUuidArgument),
                parseResult.GetValue(executionOptions.WaitOption),
                waitConfiguration.Value!,
                cancellationToken);

            if (status.ErrorResult is not null)
            {
                return await WriteApiErrorAsync(status.ErrorResult, outputMode, cancellationToken);
            }

            if (status.ErrorMessage is not null)
            {
                return await WriteCliErrorAsync(status.ErrorMessage, outputMode, cancellationToken);
            }

            var exitCode = status.TimedOut ? 1 : 0;
            if (parseResult.GetValue(executionOptions.DeleteOption))
            {
                var deleteResult = await DeleteLatestCallResultIfRequestedAsync(
                    apiClient.Value!,
                    token,
                    parseResult.GetRequiredValue(callRequestUuidArgument),
                    status,
                    outputMode,
                    cancellationToken);

                if (deleteResult.ErrorResult is not null)
                {
                    return await WriteApiErrorAsync(deleteResult.ErrorResult, outputMode, cancellationToken);
                }

                if (deleteResult.ErrorMessage is not null)
                {
                    return await WriteCliErrorAsync(deleteResult.ErrorMessage, outputMode, cancellationToken);
                }

                if (deleteResult.WarningMessage is not null)
                {
                    await WriteCliWarningAsync(deleteResult.WarningMessage, outputMode, cancellationToken);
                }

                status = ApplyDeleteOutcome(status, deleteResult);
            }

            await WriteSuccessAsync(CommandOutput.Json(status.JsonPayload!, status.HumanPayload!), outputMode, cancellationToken);
            return exitCode;
        });

        return command;
    }

    private Command CreateCallsResultsCommand()
    {
        var callRequestUuidArgument = new Argument<string>("call-request-uuid") { Description = "The call request uuid." };

        var command = new Command("results", "Show call results for a call request.");
        command.Add(callRequestUuidArgument);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputMode = GetOutputMode(parseResult);
            var apiClient = GetApiClient(parseResult);
            if (apiClient.IsError)
            {
                return await WriteCliErrorAsync(apiClient.ErrorMessage!, outputMode, cancellationToken);
            }

            var token = await RequireTokenAsync(parseResult, outputMode, cancellationToken);
            if (token is null)
            {
                return 1;
            }

            var result = await apiClient.Value!.ListCallResultsForRequestAsync(token, parseResult.GetRequiredValue(callRequestUuidArgument), cancellationToken);
            if (!result.IsSuccessStatusCode)
            {
                return await WriteApiErrorAsync(result, outputMode, cancellationToken);
            }

            if (result.JsonBody is null)
            {
                return await WriteSuccessAsync(CommandOutput.Empty(), outputMode, cancellationToken);
            }

            var humanPayload = BuildCallResultsHumanPayload(result.JsonBody);
            return await WriteSuccessAsync(CommandOutput.Json(result.JsonBody, humanPayload), outputMode, cancellationToken);
        });

        return command;
    }

    private CallCreateOptions CreateCallCreateOptions()
    {
        var organisationOption = CreateOption<string?>("--organisation", "The organisation id or exact name for the call request.");
        organisationOption.Aliases.Add("--organisation-id");
        var callMethodOption = CreateOption<string?>("--call-method", "How the call should be delivered: `phone` or `web`.");
        var phoneNumberOption = CreateOption<string?>("--phone-number", "The international phone number to call when using `phone`, for example `+61400000000`.");
        var recipientNameOption = CreateOption<string?>("--recipient-name", "The recipient's name.");
        var recipientTimezoneOption = CreateOption<string?>("--recipient-timezone", "The recipient's valid IANA timezone, for example `Australia/Melbourne`.");
        var templateUuidOption = CreateOption<string?>("--template-uuid", "The call template uuid to inherit from.");
        var templateIdOption = CreateOption<int?>("--template-id", "Optional specific call template version id.");
        var instructionsOption = CreateOption<string?>("--instructions", "Inline instructions for the call.");
        var instructionsFileOption = CreateOption<string?>("--instructions-file", "Read instructions from a file.");
        var agendaOption = CreateOption<string?>("--agenda", "Inline agenda for the call.");
        var agendaFileOption = CreateOption<string?>("--agenda-file", "Read agenda from a file.");
        var noteOption = CreateOption<string[]>("--note", "Add a call note.");
        noteOption.AllowMultipleArgumentsPerToken = true;
        var questionOption = CreateOption<string[]>("--question", "Add a result question as `id:format:text`.");
        questionOption.AllowMultipleArgumentsPerToken = true;

        return new CallCreateOptions(
            organisationOption,
            callMethodOption,
            phoneNumberOption,
            recipientNameOption,
            recipientTimezoneOption,
            templateUuidOption,
            templateIdOption,
            instructionsOption,
            instructionsFileOption,
            agendaOption,
            agendaFileOption,
            noteOption,
            questionOption);
    }

    private static void AddCallCreateOptions(Command command, CallCreateOptions options)
    {
        command.Add(options.OrganisationOption);
        command.Add(options.CallMethodOption);
        command.Add(options.PhoneNumberOption);
        command.Add(options.RecipientNameOption);
        command.Add(options.RecipientTimezoneOption);
        command.Add(options.TemplateUuidOption);
        command.Add(options.TemplateIdOption);
        command.Add(options.InstructionsOption);
        command.Add(options.InstructionsFileOption);
        command.Add(options.AgendaOption);
        command.Add(options.AgendaFileOption);
        command.Add(options.NoteOption);
        command.Add(options.QuestionOption);
    }

    private CallExecutionOptions CreateCallExecutionOptions()
    {
        var waitOption = CreateOption<bool>("--wait", "Wait until a call result exists.");
        var deleteOption = CreateOption<bool>("--delete", "Delete the latest call result after it is returned.");
        var pollEveryOption = CreateOption<string?>("--poll-every", "Polling interval such as `5s`, `1m`, `2h`, or `00:00:05`.");
        var timeoutOption = CreateOption<string?>("--timeout", "Optional timeout such as `30s`, `2m`, or `00:02:00`.");
        return new CallExecutionOptions(waitOption, deleteOption, pollEveryOption, timeoutOption);
    }

    private static void AddCallExecutionOptions(Command command, CallExecutionOptions options)
    {
        command.Add(options.WaitOption);
        command.Add(options.DeleteOption);
        command.Add(options.PollEveryOption);
        command.Add(options.TimeoutOption);
    }

    private Command CreateApiKeysCommand()
    {
        var command = new Command("api-keys", "Manage organisation API keys.");
        command.Add(CreateApiKeysListCommand());
        command.Add(CreateApiKeysGetCommand());
        command.Add(CreateApiKeysCreateCommand());
        command.Add(CreateApiKeysRevokeCommand());
        return command;
    }

    private Command CreateApiKeysListCommand()
    {
        var organisationIdOption = CreateOption<int?>("--organisation-id", "Limit results to a specific organisation id.");

        var command = new Command("list", "List visible API keys.");
        command.Add(organisationIdOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputMode = GetOutputMode(parseResult);
            var apiClient = GetApiClient(parseResult);
            if (apiClient.IsError)
            {
                return await WriteCliErrorAsync(apiClient.ErrorMessage!, outputMode, cancellationToken);
            }

            var token = await RequireTokenAsync(parseResult, outputMode, cancellationToken);
            if (token is null)
            {
                return 1;
            }

            var result = await apiClient.Value!.ListApiKeysAsync(token, parseResult.GetValue(organisationIdOption), cancellationToken);
            return await HandleApiResultAsync(result, outputMode, cancellationToken);
        });

        return command;
    }

    private Command CreateApiKeysGetCommand()
    {
        var idArgument = new Argument<string>("id") { Description = "The API key id." };

        var command = new Command("get", "Get a single API key.");
        command.Add(idArgument);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputMode = GetOutputMode(parseResult);
            var apiClient = GetApiClient(parseResult);
            if (apiClient.IsError)
            {
                return await WriteCliErrorAsync(apiClient.ErrorMessage!, outputMode, cancellationToken);
            }

            var token = await RequireTokenAsync(parseResult, outputMode, cancellationToken);
            if (token is null)
            {
                return 1;
            }

            var result = await apiClient.Value!.GetApiKeyAsync(token, parseResult.GetRequiredValue(idArgument), cancellationToken);
            return await HandleApiResultAsync(result, outputMode, cancellationToken);
        });

        return command;
    }

    private Command CreateApiKeysCreateCommand()
    {
        var organisationIdOption = CreateRequiredOption<int>("--organisation-id", "The organisation id for the new API key.");
        var permissionOption = CreateRequiredOption<string[]>("--permission", "A permission to grant to the API key.");
        permissionOption.AllowMultipleArgumentsPerToken = true;
        var expiresAtOption = CreateOption<string?>("--expires-at", "Optional ISO-8601 expiry timestamp.");

        var command = new Command("create", "Create a new API key.");
        command.Add(organisationIdOption);
        command.Add(permissionOption);
        command.Add(expiresAtOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputMode = GetOutputMode(parseResult);
            var apiClient = GetApiClient(parseResult);
            if (apiClient.IsError)
            {
                return await WriteCliErrorAsync(apiClient.ErrorMessage!, outputMode, cancellationToken);
            }

            var token = await RequireTokenAsync(parseResult, outputMode, cancellationToken);
            if (token is null)
            {
                return 1;
            }

            if (!TryParseDateTimeOffset(parseResult.GetValue(expiresAtOption), out var expiresAt))
            {
                return await WriteCliErrorAsync("Invalid --expires-at value. Expected an ISO-8601 timestamp.", outputMode, cancellationToken);
            }

            var permissions = parseResult.GetRequiredValue(permissionOption)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();

            if (permissions.Length == 0)
            {
                return await WriteCliErrorAsync("At least one non-empty --permission value is required.", outputMode, cancellationToken);
            }

            var request = new CreateApiKeyRequest
            {
                ApiKey = new ApiKeyWriteModel
                {
                    OrganisationId = parseResult.GetRequiredValue(organisationIdOption),
                    Permissions = permissions,
                    ExpiresAt = expiresAt
                }
            };

            var result = await apiClient.Value!.CreateApiKeyAsync(token, request, cancellationToken);
            return await HandleApiResultAsync(result, outputMode, cancellationToken);
        });

        return command;
    }

    private Command CreateApiKeysRevokeCommand()
    {
        var idArgument = new Argument<string>("id") { Description = "The API key id." };

        var command = new Command("revoke", "Revoke an API key.");
        command.Add(idArgument);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputMode = GetOutputMode(parseResult);
            var apiClient = GetApiClient(parseResult);
            if (apiClient.IsError)
            {
                return await WriteCliErrorAsync(apiClient.ErrorMessage!, outputMode, cancellationToken);
            }

            var token = await RequireTokenAsync(parseResult, outputMode, cancellationToken);
            if (token is null)
            {
                return 1;
            }

            var result = await apiClient.Value!.RevokeApiKeyAsync(token, parseResult.GetRequiredValue(idArgument), cancellationToken);
            return await HandleApiResultAsync(result, outputMode, cancellationToken);
        });

        return command;
    }

    private Command CreateOrganisationsCommand()
    {
        var command = new Command("organisations", "Manage organisations.");
        command.Add(CreateOrganisationsListCommand());
        command.Add(CreateOrganisationsGetCommand());
        command.Add(CreateOrganisationsCreateCommand());
        command.Add(CreateOrganisationsUpdateCommand());
        return command;
    }

    private Command CreateOrganisationsListCommand()
    {
        var offsetOption = CreateOption<int?>("--offset", "Offset the first returned organisation.");
        var orderOption = CreateOption<string?>("--order", "Sort order, for example `id DESC`.");
        var sizeOption = CreateOption<int?>("--size", "Maximum number of returned organisations.");
        var nameOption = CreateOption<string?>("--name", "Limit results to a matching organisation name.");

        var command = new Command("list", "List visible organisations.");
        command.Add(offsetOption);
        command.Add(orderOption);
        command.Add(sizeOption);
        command.Add(nameOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputMode = GetOutputMode(parseResult);
            var apiClient = GetApiClient(parseResult);
            if (apiClient.IsError)
            {
                return await WriteCliErrorAsync(apiClient.ErrorMessage!, outputMode, cancellationToken);
            }

            var token = await RequireTokenAsync(parseResult, outputMode, cancellationToken);
            if (token is null)
            {
                return 1;
            }

            var query = new ListOrganisationsQuery
            {
                Offset = parseResult.GetValue(offsetOption),
                Order = parseResult.GetValue(orderOption),
                Size = parseResult.GetValue(sizeOption),
                Name = parseResult.GetValue(nameOption)
            };

            var result = await apiClient.Value!.ListOrganisationsAsync(token, query, cancellationToken);
            return await HandleApiResultAsync(result, outputMode, cancellationToken);
        });

        return command;
    }

    private Command CreateOrganisationsGetCommand()
    {
        var idArgument = new Argument<int>("organisation-id") { Description = "The organisation id." };

        var command = new Command("get", "Get a single organisation.");
        command.Add(idArgument);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputMode = GetOutputMode(parseResult);
            var apiClient = GetApiClient(parseResult);
            if (apiClient.IsError)
            {
                return await WriteCliErrorAsync(apiClient.ErrorMessage!, outputMode, cancellationToken);
            }

            var token = await RequireTokenAsync(parseResult, outputMode, cancellationToken);
            if (token is null)
            {
                return 1;
            }

            var result = await apiClient.Value!.GetOrganisationAsync(token, parseResult.GetRequiredValue(idArgument), cancellationToken);
            return await HandleApiResultAsync(result, outputMode, cancellationToken);
        });

        return command;
    }

    private Command CreateOrganisationsCreateCommand()
    {
        var options = CreateOrganisationWriteOptions();
        var command = new Command("create", "Create a new organisation.");
        AddOrganisationWriteOptions(command, options);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputMode = GetOutputMode(parseResult);
            var apiClient = GetApiClient(parseResult);
            if (apiClient.IsError)
            {
                return await WriteCliErrorAsync(apiClient.ErrorMessage!, outputMode, cancellationToken);
            }

            var token = await RequireTokenAsync(parseResult, outputMode, cancellationToken);
            if (token is null)
            {
                return 1;
            }

            var request = BuildOrganisationRequest(parseResult, options);
            var result = await apiClient.Value!.CreateOrganisationAsync(token, request, cancellationToken);
            return await HandleApiResultAsync(result, outputMode, cancellationToken);
        });

        return command;
    }

    private Command CreateOrganisationsUpdateCommand()
    {
        var idArgument = new Argument<int>("organisation-id") { Description = "The organisation id." };
        var options = CreateOrganisationWriteOptions();

        var command = new Command("update", "Update an existing organisation.");
        command.Add(idArgument);
        AddOrganisationWriteOptions(command, options);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputMode = GetOutputMode(parseResult);
            var apiClient = GetApiClient(parseResult);
            if (apiClient.IsError)
            {
                return await WriteCliErrorAsync(apiClient.ErrorMessage!, outputMode, cancellationToken);
            }

            var token = await RequireTokenAsync(parseResult, outputMode, cancellationToken);
            if (token is null)
            {
                return 1;
            }

            var request = BuildOrganisationRequest(parseResult, options);
            var result = await apiClient.Value!.PatchOrganisationAsync(token, parseResult.GetRequiredValue(idArgument), request, cancellationToken);
            return await HandleApiResultAsync(result, outputMode, cancellationToken);
        });

        return command;
    }

    private Command CreateUsersCommand()
    {
        var command = new Command("users", "Manage users.");
        command.Add(CreateUsersListCommand());
        command.Add(CreateUsersMeCommand());
        command.Add(CreateUsersGetCommand());
        command.Add(CreateUsersCreateCommand());
        command.Add(CreateUsersUpdateCommand());
        return command;
    }

    private Command CreateUsersListCommand()
    {
        var offsetOption = CreateOption<int?>("--offset", "Offset the first returned user.");
        var orderOption = CreateOption<string?>("--order", "Sort order, for example `id DESC`.");
        var sizeOption = CreateOption<int?>("--size", "Maximum number of returned users.");
        var organisationIdOption = CreateOption<int?>("--organisation-id", "Limit results to a specific organisation id.");

        var command = new Command("list", "List visible users.");
        command.Add(offsetOption);
        command.Add(orderOption);
        command.Add(sizeOption);
        command.Add(organisationIdOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputMode = GetOutputMode(parseResult);
            var apiClient = GetApiClient(parseResult);
            if (apiClient.IsError)
            {
                return await WriteCliErrorAsync(apiClient.ErrorMessage!, outputMode, cancellationToken);
            }

            var token = await RequireTokenAsync(parseResult, outputMode, cancellationToken);
            if (token is null)
            {
                return 1;
            }

            var query = new ListUsersQuery
            {
                Offset = parseResult.GetValue(offsetOption),
                Order = parseResult.GetValue(orderOption),
                Size = parseResult.GetValue(sizeOption),
                OrganisationId = parseResult.GetValue(organisationIdOption)
            };

            var result = await apiClient.Value!.ListUsersAsync(token, query, cancellationToken);
            return await HandleApiResultAsync(result, outputMode, cancellationToken);
        });

        return command;
    }

    private Command CreateUsersMeCommand()
    {
        var command = new Command("me", "Get the current authenticated user.");
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputMode = GetOutputMode(parseResult);
            var apiClient = GetApiClient(parseResult);
            if (apiClient.IsError)
            {
                return await WriteCliErrorAsync(apiClient.ErrorMessage!, outputMode, cancellationToken);
            }

            var token = await RequireTokenAsync(parseResult, outputMode, cancellationToken);
            if (token is null)
            {
                return 1;
            }

            var result = await apiClient.Value!.GetCurrentUserAsync(token, cancellationToken);
            return await HandleApiResultAsync(result, outputMode, cancellationToken);
        });

        return command;
    }

    private Command CreateUsersGetCommand()
    {
        var nameArgument = new Argument<string>("name") { Description = "The user name." };

        var command = new Command("get", "Get a single user.");
        command.Add(nameArgument);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputMode = GetOutputMode(parseResult);
            var apiClient = GetApiClient(parseResult);
            if (apiClient.IsError)
            {
                return await WriteCliErrorAsync(apiClient.ErrorMessage!, outputMode, cancellationToken);
            }

            var token = await RequireTokenAsync(parseResult, outputMode, cancellationToken);
            if (token is null)
            {
                return 1;
            }

            var result = await apiClient.Value!.GetUserAsync(token, parseResult.GetRequiredValue(nameArgument), cancellationToken);
            return await HandleApiResultAsync(result, outputMode, cancellationToken);
        });

        return command;
    }

    private Command CreateUsersCreateCommand()
    {
        var options = CreateUserWriteOptions(requireCountry: true, useNewNameOption: false);

        var command = new Command("create", "Create a new user.");
        AddUserWriteOptions(command, options);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputMode = GetOutputMode(parseResult);
            var apiClient = GetApiClient(parseResult);
            if (apiClient.IsError)
            {
                return await WriteCliErrorAsync(apiClient.ErrorMessage!, outputMode, cancellationToken);
            }

            var token = await RequireTokenAsync(parseResult, outputMode, cancellationToken);
            if (token is null)
            {
                return 1;
            }

            if (!TryBuildUserRequest(parseResult, options, out var request, out var error))
            {
                return await WriteCliErrorAsync(error!, outputMode, cancellationToken);
            }

            var result = await apiClient.Value!.CreateUserAsync(token, request!, cancellationToken);
            return await HandleApiResultAsync(result, outputMode, cancellationToken);
        });

        return command;
    }

    private Command CreateUsersUpdateCommand()
    {
        var targetNameArgument = new Argument<string>("name") { Description = "The user name to update." };
        var options = CreateUserWriteOptions(requireCountry: false, useNewNameOption: true);

        var command = new Command("update", "Update an existing user.");
        command.Add(targetNameArgument);
        AddUserWriteOptions(command, options);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputMode = GetOutputMode(parseResult);
            var apiClient = GetApiClient(parseResult);
            if (apiClient.IsError)
            {
                return await WriteCliErrorAsync(apiClient.ErrorMessage!, outputMode, cancellationToken);
            }

            var token = await RequireTokenAsync(parseResult, outputMode, cancellationToken);
            if (token is null)
            {
                return 1;
            }

            var targetName = parseResult.GetRequiredValue(targetNameArgument);
            if (!TryBuildUserRequest(parseResult, options, out var request, out var error, targetName))
            {
                return await WriteCliErrorAsync(error!, outputMode, cancellationToken);
            }

            var result = await apiClient.Value!.PatchUserAsync(token, targetName, request!, cancellationToken);
            return await HandleApiResultAsync(result, outputMode, cancellationToken);
        });

        return command;
    }

    private OrganisationWriteOptions CreateOrganisationWriteOptions()
    {
        var nameOption = CreateRequiredOption<string>("--name", "The organisation name.");
        var facilityOption = CreateOption<string[]>("--facility", "A facility name to include for the organisation.");
        facilityOption.AllowMultipleArgumentsPerToken = true;
        var notificationTemplateOption = CreateOption<string?>("--notification-template", "Optional notification template.");
        var authIpOption = CreateOption<string[]>("--auth-ip", "An authorised IP address.");
        authIpOption.AllowMultipleArgumentsPerToken = true;
        var adminUserNameOption = CreateOption<string?>("--admin-user-name", "Optional admin user name override.");
        var createDefaultResourcesOption = CreateOption<bool?>("--create-default-resources", "Whether to create suggested default resources.");
        var alertTitleTemplateOption = CreateOption<string?>("--alert-title-template", "Optional alert notification title template.");
        var personalOrganisationOption = CreateOption<bool?>("--personal-organisation", "Whether the organisation is for personal use.");

        return new OrganisationWriteOptions(
            nameOption,
            facilityOption,
            notificationTemplateOption,
            authIpOption,
            adminUserNameOption,
            createDefaultResourcesOption,
            alertTitleTemplateOption,
            personalOrganisationOption);
    }

    private static void AddOrganisationWriteOptions(Command command, OrganisationWriteOptions options)
    {
        command.Add(options.NameOption);
        command.Add(options.FacilityOption);
        command.Add(options.NotificationTemplateOption);
        command.Add(options.AuthIpOption);
        command.Add(options.AdminUserNameOption);
        command.Add(options.CreateDefaultResourcesOption);
        command.Add(options.AlertTitleTemplateOption);
        command.Add(options.PersonalOrganisationOption);
    }

    private static OrganisationWriteRequest BuildOrganisationRequest(ParseResult parseResult, OrganisationWriteOptions options)
    {
        var facilities = parseResult.GetValue(options.FacilityOption) ?? [];
        var authIps = parseResult.GetValue(options.AuthIpOption) ?? [];
        var alertTitleTemplate = parseResult.GetValue(options.AlertTitleTemplateOption);

        return new OrganisationWriteRequest
        {
            Organisation = new OrganisationWriteModel
            {
                Name = parseResult.GetRequiredValue(options.NameOption),
                Facilities = facilities.Length == 0 ? null : facilities,
                NotificationTemplate = parseResult.GetValue(options.NotificationTemplateOption),
                AuthIps = authIps.Length == 0 ? null : authIps
            },
            AdminUserName = parseResult.GetValue(options.AdminUserNameOption),
            CreateDefaultResources = parseResult.GetValue(options.CreateDefaultResourcesOption),
            AlertNotificationTemplate = string.IsNullOrWhiteSpace(alertTitleTemplate)
                ? null
                : new AlertNotificationTemplateModel { Title = alertTitleTemplate },
            PersonalOrganisation = parseResult.GetValue(options.PersonalOrganisationOption)
        };
    }

    private UserWriteOptions CreateUserWriteOptions(bool requireCountry, bool useNewNameOption)
    {
        var nameOption = CreateOption<string>(useNewNameOption ? "--new-name" : "--name", useNewNameOption ? "Optional replacement user name." : "The user name.");
        nameOption.Required = !useNewNameOption;
        var passwordOption = CreateRequiredOption<string>("--password", "The user password.");
        var countryOption = CreateOption<string?>("--country", "The user's ISO alpha-2 country code.");
        countryOption.Required = requireCountry;
        var contactIdOption = CreateOption<int?>("--contact-id", "Use an existing contact id.");
        var contactNameOption = CreateOption<string?>("--contact-name", "Create or update the user's contact name.");
        var contactEmailOption = CreateOption<string?>("--contact-email", "Create or update the user's contact email.");
        var contactPhoneOption = CreateOption<string?>("--contact-phone", "Optional contact phone number. Use an international number, or pair a national-format number with `--country`.");

        return new UserWriteOptions(
            nameOption,
            passwordOption,
            countryOption,
            contactIdOption,
            contactNameOption,
            contactEmailOption,
            contactPhoneOption);
    }

    private static void AddUserWriteOptions(Command command, UserWriteOptions options)
    {
        command.Add(options.NameOption);
        command.Add(options.PasswordOption);
        command.Add(options.CountryOption);
        command.Add(options.ContactIdOption);
        command.Add(options.ContactNameOption);
        command.Add(options.ContactEmailOption);
        command.Add(options.ContactPhoneOption);
    }

    private static bool TryBuildUserRequest(
        ParseResult parseResult,
        UserWriteOptions options,
        out UserWriteRequest? request,
        out string? error,
        string? defaultName = null)
    {
        request = null;
        error = null;

        var requestName = parseResult.GetValue(options.NameOption) ?? defaultName;
        if (string.IsNullOrWhiteSpace(requestName))
        {
            error = "A user name is required.";
            return false;
        }

        var country = parseResult.GetValue(options.CountryOption);
        if (!string.IsNullOrWhiteSpace(country) && !IsCountryCode(country))
        {
            error = "Invalid --country value. Expected an uppercase ISO alpha-2 country code.";
            return false;
        }

        var contactId = parseResult.GetValue(options.ContactIdOption);
        var contactName = parseResult.GetValue(options.ContactNameOption);
        var contactEmail = parseResult.GetValue(options.ContactEmailOption);
        var contactPhone = parseResult.GetValue(options.ContactPhoneOption);

        var usesNestedContact = !string.IsNullOrWhiteSpace(contactName)
            || !string.IsNullOrWhiteSpace(contactEmail)
            || !string.IsNullOrWhiteSpace(contactPhone);

        if (contactId is not null && usesNestedContact)
        {
            error = "--contact-id cannot be used together with --contact-name, --contact-email, or --contact-phone.";
            return false;
        }

        if (usesNestedContact && (string.IsNullOrWhiteSpace(contactName) || string.IsNullOrWhiteSpace(contactEmail)))
        {
            error = "When using nested contact fields, both --contact-name and --contact-email are required.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(contactPhone)
            && ApiFieldValidator.ValidateContactPhoneNumber(contactPhone, country) is { } contactPhoneError)
        {
            error = $"Invalid --contact-phone value. {contactPhoneError}";
            return false;
        }

        UserContactWriteModel? contact = null;
        if (usesNestedContact)
        {
            var details = new List<ContactDetailWriteModel>
            {
                new()
                {
                    Type = "email",
                    Value = contactEmail!,
                    Filter = []
                }
            };

            if (!string.IsNullOrWhiteSpace(contactPhone))
            {
                details.Add(new ContactDetailWriteModel
                {
                    Type = "phone",
                    Value = contactPhone,
                    Filter = []
                });
            }

            contact = new UserContactWriteModel
            {
                Name = contactName!,
                Details = details
            };
        }

        request = new UserWriteRequest
        {
            User = new UserWriteModel
            {
                Name = requestName,
                Password = parseResult.GetRequiredValue(options.PasswordOption),
                Country = country,
                ContactId = contactId,
                Contact = contact
            }
        };

        return true;
    }

    private async Task<CallCreateInputResolution> ResolveCallCreateInputAsync(
        IHalleyApiClient apiClient,
        string token,
        ParseResult parseResult,
        CallCreateOptions options,
        OutputMode outputMode,
        CancellationToken cancellationToken)
    {
        var wasPromptedInteractively = !HasCallCreateInput(parseResult, options);
        CallCreateCandidateResolution candidate;
        if (wasPromptedInteractively)
        {
            if (!_interactiveUi.IsInteractive)
            {
                return CallCreateInputResolution.Error("`calls create` without create options requires an interactive terminal. Provide the call options explicitly when running non-interactively.");
            }

            CurrentLogger.Information("Prompting interactively for a call request.");
            candidate = _interactiveUi.SupportsCallCreateWizard
                ? await PromptForCallCreateWizardAsync(apiClient, token, cancellationToken)
                : await PromptForCallCreateInputAsync(apiClient, token, cancellationToken);
        }
        else
        {
            candidate = await ReadCallCreateInputFromOptionsAsync(parseResult, options, cancellationToken);
        }

        if (candidate.ErrorResult is not null)
        {
            return CallCreateInputResolution.ApiError(candidate.ErrorResult);
        }

        if (candidate.ErrorMessage is not null)
        {
            return CallCreateInputResolution.Error(candidate.ErrorMessage);
        }

        var validatedInput = await ValidateAndResolveCallCreateInputAsync(apiClient, token, candidate.Candidate!, cancellationToken);
        if (validatedInput.Input is null || validatedInput.ErrorMessage is not null || validatedInput.ErrorResult is not null)
        {
            return validatedInput;
        }

        return CallCreateInputResolution.Success(validatedInput.Input, wasPromptedInteractively, candidate.ShouldReturnCommandOnly);
    }

    private static bool HasCallCreateInput(ParseResult parseResult, CallCreateOptions options) =>
        WasOptionProvided(parseResult, options.OrganisationOption)
        || WasOptionProvided(parseResult, options.CallMethodOption)
        || WasOptionProvided(parseResult, options.PhoneNumberOption)
        || WasOptionProvided(parseResult, options.RecipientNameOption)
        || WasOptionProvided(parseResult, options.RecipientTimezoneOption)
        || WasOptionProvided(parseResult, options.TemplateUuidOption)
        || WasOptionProvided(parseResult, options.TemplateIdOption)
        || WasOptionProvided(parseResult, options.InstructionsOption)
        || WasOptionProvided(parseResult, options.InstructionsFileOption)
        || WasOptionProvided(parseResult, options.AgendaOption)
        || WasOptionProvided(parseResult, options.AgendaFileOption)
        || WasOptionProvided(parseResult, options.NoteOption)
        || WasOptionProvided(parseResult, options.QuestionOption);

    private async Task<CallCreateCandidateResolution> ReadCallCreateInputFromOptionsAsync(
        ParseResult parseResult,
        CallCreateOptions options,
        CancellationToken cancellationToken)
    {
        if (WasOptionProvided(parseResult, options.InstructionsOption) && WasOptionProvided(parseResult, options.InstructionsFileOption))
        {
            return CallCreateCandidateResolution.Error("--instructions cannot be used together with --instructions-file.");
        }

        if (WasOptionProvided(parseResult, options.AgendaOption) && WasOptionProvided(parseResult, options.AgendaFileOption))
        {
            return CallCreateCandidateResolution.Error("--agenda cannot be used together with --agenda-file.");
        }

        string? instructions;
        try
        {
            instructions = await ResolveTextInputAsync(
                parseResult.GetValue(options.InstructionsOption),
                parseResult.GetValue(options.InstructionsFileOption),
                cancellationToken);
        }
        catch (IOException ex)
        {
            return CallCreateCandidateResolution.Error($"Failed to read instructions file: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return CallCreateCandidateResolution.Error($"Failed to read instructions file: {ex.Message}");
        }

        string? agenda;
        try
        {
            agenda = await ResolveTextInputAsync(
                parseResult.GetValue(options.AgendaOption),
                parseResult.GetValue(options.AgendaFileOption),
                cancellationToken);
        }
        catch (IOException ex)
        {
            return CallCreateCandidateResolution.Error($"Failed to read agenda file: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return CallCreateCandidateResolution.Error($"Failed to read agenda file: {ex.Message}");
        }

        if (!TryParseCallQuestions(parseResult.GetValue(options.QuestionOption) ?? [], out var questions, out var error))
        {
            return CallCreateCandidateResolution.Error(error!);
        }

        var candidate = new CallCreateCandidate(
            parseResult.GetValue(options.OrganisationOption),
            parseResult.GetValue(options.CallMethodOption),
            parseResult.GetValue(options.PhoneNumberOption),
            parseResult.GetValue(options.RecipientNameOption),
            parseResult.GetValue(options.RecipientTimezoneOption),
            parseResult.GetValue(options.TemplateUuidOption),
            parseResult.GetValue(options.TemplateIdOption),
            instructions,
            agenda,
            (parseResult.GetValue(options.NoteOption) ?? [])
                .Select(NormalizeOptionalText)
                .Where(value => value is not null)
                .Cast<string>()
                .ToArray(),
            questions);

        return CallCreateCandidateResolution.Success(candidate);
    }

    private async Task<CallCreateCandidateResolution> PromptForCallCreateInputAsync(
        IHalleyApiClient apiClient,
        string token,
        CancellationToken cancellationToken)
    {
        var mode = await PromptForChoiceAsync(
            "Call mode [template/manual/template+manual]: ",
            static value => value switch
            {
                "template" or "t" => "template",
                "manual" or "m" => "manual",
                "template+manual" or "both" or "b" => "template+manual",
                _ => null
            },
            "Enter `template`, `manual`, or `template+manual`.",
            CallModeSuggestions,
            cancellationToken);

        if (mode is null)
        {
            return CallCreateCandidateResolution.Error("Interactive call creation was cancelled.");
        }

        var useTemplate = mode is "template" or "template+manual";
        var useManual = mode is "manual" or "template+manual";

        var organisationSuggestions = await LoadOrganisationSuggestionsAsync(apiClient, token, cancellationToken);
        if (organisationSuggestions.ErrorResult is not null)
        {
            return CallCreateCandidateResolution.ApiError(organisationSuggestions.ErrorResult);
        }

        var organisationReference = await PromptForOrganisationAsync(apiClient, token, organisationSuggestions.Suggestions, cancellationToken);
        if (organisationReference.ErrorResult is not null)
        {
            return CallCreateCandidateResolution.ApiError(organisationReference.ErrorResult);
        }

        if (organisationReference.ErrorMessage is not null)
        {
            return CallCreateCandidateResolution.Error(organisationReference.ErrorMessage);
        }

        var organisation = organisationReference.Organisation!;

        var callMethod = await PromptForChoiceAsync(
            "Call method [phone/web]: ",
            static value => value is "phone" or "web" ? value : null,
            "Enter `phone` or `web`.",
            CallMethodSuggestions,
            cancellationToken);
        if (callMethod is null)
        {
            return CallCreateCandidateResolution.Error("Interactive call creation was cancelled.");
        }

        string? phoneNumber = null;
        if (callMethod == "phone")
        {
            phoneNumber = await PromptForRequiredLineAsync(
                "Phone number: ",
                cancellationToken,
                PhoneNumberSuggestions,
                "Enter a valid international phone number such as `+61400000000`.",
                ApiFieldValidator.ValidateInternationalPhoneNumber);
            if (phoneNumber is null)
            {
                return CallCreateCandidateResolution.Error("Interactive call creation was cancelled.");
            }
        }

        var recipientName = await PromptForRequiredLineAsync("Recipient name: ", cancellationToken, RecipientNameSuggestions, "Enter the person who should receive the call.");
        if (recipientName is null)
        {
            return CallCreateCandidateResolution.Error("Interactive call creation was cancelled.");
        }

        var recipientTimezone = await PromptForRequiredLineAsync(
            "Recipient timezone: ",
            cancellationToken,
            GetTimezoneSuggestions(),
            "Use a valid IANA timezone such as `Australia/Melbourne`.",
            ApiFieldValidator.ValidateIanaTimezone);
        if (recipientTimezone is null)
        {
            return CallCreateCandidateResolution.Error("Interactive call creation was cancelled.");
        }

        string? templateUuid = null;
        int? templateId = null;
        if (useTemplate)
        {
            while (true)
            {
                var templateSuggestions = await LoadTemplateSuggestionsAsync(apiClient, token, organisation.Id, cancellationToken);
                if (templateSuggestions.ErrorResult is not null)
                {
                    return CallCreateCandidateResolution.ApiError(templateSuggestions.ErrorResult);
                }

                templateUuid = await PromptForRequiredLineAsync(
                    "Template (name or uuid): ",
                    cancellationToken,
                    templateSuggestions.Suggestions,
                    "Press Tab to cycle through visible templates.");
                if (templateUuid is null)
                {
                    return CallCreateCandidateResolution.Error("Interactive call creation was cancelled.");
                }

                var versionSuggestions = await LoadTemplateVersionSuggestionsAsync(apiClient, token, organisation.Id, templateUuid, cancellationToken);
                templateId = await PromptForOptionalIntAsync("Template id (optional): ", cancellationToken, versionSuggestions, "Leave blank to use the latest visible version.");
                if (templateId == CallPromptSentinel.CancelledInt)
                {
                    return CallCreateCandidateResolution.Error("Interactive call creation was cancelled.");
                }

                var templateResolution = await ResolveTemplateReferenceAsync(apiClient, token, organisation.Id, templateUuid, templateId, cancellationToken);
                if (templateResolution.ErrorResult is not null)
                {
                    return CallCreateCandidateResolution.ApiError(templateResolution.ErrorResult);
                }

                if (templateResolution.ErrorMessage is null)
                {
                    templateUuid = templateResolution.Template!.Uuid;
                    templateId = templateResolution.Template.Id;
                    break;
                }

                await WriteCliWarningAsync(templateResolution.ErrorMessage, OutputMode.Human, cancellationToken);
            }
        }

        string? instructions = null;
        string? agenda = null;
        IReadOnlyList<string> notes = [];
        IReadOnlyList<CallQuestionInput> questions = [];

        if (useManual)
        {
            while (true)
            {
                var instructionsValue = await _interactiveUi.ReadMultilineAsync(
                    _stderr,
                    "Instructions",
                    InstructionsSuggestions,
                    "Describe how the agent should behave for the call.",
                    cancellationToken);
                if (instructionsValue is null)
                {
                    return CallCreateCandidateResolution.Error("Interactive call creation was cancelled.");
                }

                var agendaValue = await _interactiveUi.ReadMultilineAsync(
                    _stderr,
                    "Agenda",
                    AgendaSuggestions,
                    "List the steps the agent should take. One step per line works well.",
                    cancellationToken);
                if (agendaValue is null)
                {
                    return CallCreateCandidateResolution.Error("Interactive call creation was cancelled.");
                }

                instructions = NormalizeOptionalText(instructionsValue);
                agenda = NormalizeOptionalText(agendaValue);

                var notesResolution = await PromptForNotesAsync(cancellationToken);
                if (notesResolution.ErrorMessage is not null)
                {
                    return CallCreateCandidateResolution.Error(notesResolution.ErrorMessage);
                }

                notes = notesResolution.Notes;

                var questionsResolution = await PromptForQuestionsAsync(cancellationToken);
                if (questionsResolution.ErrorMessage is not null)
                {
                    return CallCreateCandidateResolution.Error(questionsResolution.ErrorMessage);
                }

                questions = questionsResolution.Questions;

                if (useTemplate || HasManualCallContent(instructions, agenda, questions))
                {
                    break;
                }

                await _stderr.WriteLineAsync("Manual calls require instructions, agenda, or at least one question.".AsMemory(), cancellationToken);
            }
        }

        return CallCreateCandidateResolution.Success(new CallCreateCandidate(
            organisation.Reference,
            callMethod,
            phoneNumber,
            recipientName,
            recipientTimezone,
            templateUuid,
            templateId,
            instructions,
            agenda,
            notes,
            questions));
    }

    private async Task<CallCreateCandidateResolution> PromptForCallCreateWizardAsync(
        IHalleyApiClient apiClient,
        string token,
        CancellationToken cancellationToken)
    {
        var organizations = await LoadOrganisationSuggestionsAsync(apiClient, token, cancellationToken);
        if (organizations.ErrorResult is not null)
        {
            return CallCreateCandidateResolution.ApiError(organizations.ErrorResult);
        }

        var request = new InteractiveCallCreateRequest(
            organizations.Suggestions,
            GetTimezoneSuggestions(),
            async (organisationReference, innerCancellationToken) =>
            {
                var resolution = await ResolveOrganisationReferenceAsync(apiClient, token, organisationReference, innerCancellationToken);
                if (resolution.Organisation is null)
                {
                    return [];
                }

                return (await LoadTemplateSuggestionsAsync(apiClient, token, resolution.Organisation.Id, innerCancellationToken)).Suggestions;
            },
            async (organisationReference, templateReference, innerCancellationToken) =>
            {
                var resolution = await ResolveOrganisationReferenceAsync(apiClient, token, organisationReference, innerCancellationToken);
                if (resolution.Organisation is null)
                {
                    return [];
                }

                return await LoadTemplateVersionSuggestionsAsync(apiClient, token, resolution.Organisation.Id, templateReference, innerCancellationToken);
            });

        var result = await _interactiveUi.RunCallCreateWizardAsync(request, cancellationToken);
        if (result.Cancelled)
        {
            return CallCreateCandidateResolution.Error("Interactive call creation was cancelled.");
        }

        return CallCreateCandidateResolution.Success(new CallCreateCandidate(
            result.OrganisationReference,
            result.CallMethod,
            result.PhoneNumber,
            result.RecipientName,
            result.RecipientTimezone,
            result.TemplateReference,
            result.TemplateId,
            result.Instructions,
            result.Agenda,
            result.Notes,
            result.Questions.Select(question => new CallQuestionInput(question.Id, question.Text, question.Format)).ToArray()), result.ShowCommand);
    }

    private async Task<CallNotesResolution> PromptForNotesAsync(CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        while (true)
        {
            var addNote = await PromptForYesNoAsync("Add a note? [y/N]: ", cancellationToken, helpText: "Choose whether to add another call note.");
            if (addNote is null)
            {
                return CallNotesResolution.Error("Interactive call creation was cancelled.");
            }

            if (addNote == false)
            {
                break;
            }

            var note = await PromptForRequiredLineAsync("Note: ", cancellationToken, NoteSuggestions, "Add any context the agent may need.");
            if (note is null)
            {
                return CallNotesResolution.Error("Interactive call creation was cancelled.");
            }

            notes.Add(note);
        }

        return CallNotesResolution.Success(notes);
    }

    private async Task<CallQuestionsResolution> PromptForQuestionsAsync(CancellationToken cancellationToken)
    {
        var questions = new List<CallQuestionInput>();
        while (true)
        {
            var addQuestion = await PromptForYesNoAsync("Add a result question? [y/N]: ", cancellationToken, helpText: "Choose whether to add another result question.");
            if (addQuestion is null)
            {
                return CallQuestionsResolution.Error("Interactive call creation was cancelled.");
            }

            if (addQuestion == false)
            {
                break;
            }

            var id = await PromptForPositiveIntAsync("Question id: ", cancellationToken, QuestionIdSuggestions, "Use a positive integer. Question ids must be unique.");
            if (id is null)
            {
                return CallQuestionsResolution.Error("Interactive call creation was cancelled.");
            }

            var format = await PromptForChoiceAsync(
                "Question format [string/boolean]: ",
                static value => value is "string" or "boolean" ? value : null,
                "Enter `string` or `boolean`.",
                QuestionFormatSuggestions,
                cancellationToken);
            if (format is null)
            {
                return CallQuestionsResolution.Error("Interactive call creation was cancelled.");
            }

            var text = await PromptForRequiredLineAsync("Question text: ", cancellationToken, QuestionTextSuggestions, "Describe what the call result should capture.");
            if (text is null)
            {
                return CallQuestionsResolution.Error("Interactive call creation was cancelled.");
            }

            questions.Add(new CallQuestionInput(id.Value, text, format));
        }

        return CallQuestionsResolution.Success(questions);
    }

    private async Task<CallCreateInputResolution> ValidateAndResolveCallCreateInputAsync(
        IHalleyApiClient apiClient,
        string token,
        CallCreateCandidate candidate,
        CancellationToken cancellationToken)
    {
        var callMethod = NormalizeOptionalText(candidate.CallMethod)?.ToLowerInvariant();
        if (callMethod is not "phone" and not "web")
        {
            return CallCreateInputResolution.Error("A valid --call-method value is required. Expected `phone` or `web`.");
        }

        var recipientName = NormalizeOptionalText(candidate.RecipientName);
        if (string.IsNullOrWhiteSpace(recipientName))
        {
            return CallCreateInputResolution.Error("A --recipient-name value is required.");
        }

        var recipientTimezone = NormalizeOptionalText(candidate.RecipientTimezone);
        if (string.IsNullOrWhiteSpace(recipientTimezone))
        {
            return CallCreateInputResolution.Error("A --recipient-timezone value is required.");
        }

        if (ApiFieldValidator.ValidateIanaTimezone(recipientTimezone) is { } timezoneError)
        {
            return CallCreateInputResolution.Error($"Invalid --recipient-timezone value. {timezoneError}");
        }

        var phoneNumber = NormalizeOptionalText(candidate.PhoneNumber);
        if (callMethod == "phone" && string.IsNullOrWhiteSpace(phoneNumber))
        {
            return CallCreateInputResolution.Error("`--phone-number` is required when `--call-method phone` is used.");
        }

        if (callMethod == "web" && !string.IsNullOrWhiteSpace(phoneNumber))
        {
            return CallCreateInputResolution.Error("`--phone-number` cannot be used when `--call-method web` is selected.");
        }

        if (callMethod == "phone" && ApiFieldValidator.ValidateInternationalPhoneNumber(phoneNumber) is { } phoneNumberError)
        {
            return CallCreateInputResolution.Error($"Invalid --phone-number value. {phoneNumberError}");
        }

        var instructions = NormalizeOptionalText(candidate.Instructions);
        var agenda = NormalizeOptionalText(candidate.Agenda);
        var notes = candidate.Notes
            .Select(NormalizeOptionalText)
            .Where(value => value is not null)
            .Cast<string>()
            .ToArray();
        var questions = candidate.Questions.ToArray();

        if (questions.GroupBy(question => question.Id).Any(group => group.Count() > 1))
        {
            return CallCreateInputResolution.Error("Question ids must be unique.");
        }

        if (questions.Any(question => question.Id <= 0))
        {
            return CallCreateInputResolution.Error("Question ids must be positive integers.");
        }

        if (questions.Any(question => question.Format is not "string" and not "boolean"))
        {
            return CallCreateInputResolution.Error("Question formats must be `string` or `boolean`.");
        }

        var templateReference = NormalizeOptionalText(candidate.TemplateReference);
        if (candidate.TemplateId is not null && string.IsNullOrWhiteSpace(templateReference))
        {
            return CallCreateInputResolution.Error("`--template-id` requires `--template-uuid`.");
        }

        if (string.IsNullOrWhiteSpace(templateReference) && !HasManualCallContent(instructions, agenda, questions))
        {
            return CallCreateInputResolution.Error("Provide `--template-uuid` or at least one of `--instructions`, `--instructions-file`, `--agenda`, `--agenda-file`, or `--question`.");
        }

        var organisationReference = NormalizeOptionalText(candidate.OrganisationReference);
        if (string.IsNullOrWhiteSpace(organisationReference))
        {
            return CallCreateInputResolution.Error("A `--organisation` value is required.");
        }

        var organisationResolution = await ResolveOrganisationReferenceAsync(apiClient, token, organisationReference, cancellationToken);
        if (organisationResolution.ErrorResult is not null)
        {
            return CallCreateInputResolution.ApiError(organisationResolution.ErrorResult);
        }

        if (organisationResolution.ErrorMessage is not null)
        {
            return CallCreateInputResolution.Error(organisationResolution.ErrorMessage);
        }

        var organisation = organisationResolution.Organisation!;
        if (!organisation.ActiveLicenseHotline)
        {
            return CallCreateInputResolution.Error($"Organisation `{organisation.Name}` does not have an active Hotline license and cannot create calls.");
        }

        ValidatedTemplateReference? template = null;
        if (!string.IsNullOrWhiteSpace(templateReference))
        {
            var templateResolution = await ResolveTemplateReferenceAsync(
                apiClient,
                token,
                organisation.Id,
                templateReference,
                candidate.TemplateId,
                cancellationToken);

            if (templateResolution.ErrorResult is not null)
            {
                return CallCreateInputResolution.ApiError(templateResolution.ErrorResult);
            }

            if (templateResolution.ErrorMessage is not null)
            {
                return CallCreateInputResolution.Error(templateResolution.ErrorMessage);
            }

            template = templateResolution.Template!;
        }

        return CallCreateInputResolution.Success(new CallCreateInput(
            organisation.Id,
            organisation.Name,
            callMethod,
            phoneNumber,
            recipientName,
            recipientTimezone,
            template?.Uuid,
            template?.Id,
            instructions,
            agenda,
            notes,
            questions));
    }

    private static bool HasManualCallContent(string? instructions, string? agenda, IReadOnlyList<CallQuestionInput> questions) =>
        !string.IsNullOrWhiteSpace(instructions)
        || !string.IsNullOrWhiteSpace(agenda)
        || questions.Count > 0;

    private async Task<OrganisationReferenceResolution> PromptForOrganisationAsync(
        IHalleyApiClient apiClient,
        string token,
        IReadOnlyList<InteractiveSuggestion> suggestions,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var value = await PromptForRequiredLineAsync(
                "Organisation: ",
                cancellationToken,
                suggestions,
                "Type an organisation name or id. Press Tab to autocomplete.");

            if (value is null)
            {
                return OrganisationReferenceResolution.Error("Interactive call creation was cancelled.");
            }

            var resolution = await ResolveOrganisationReferenceAsync(apiClient, token, value, cancellationToken);
            if (resolution.ErrorResult is not null)
            {
                return resolution;
            }

            if (resolution.ErrorMessage is null)
            {
                return resolution;
            }

            await WriteCliWarningAsync(resolution.ErrorMessage, OutputMode.Human, cancellationToken);
        }
    }

    private async Task<InteractiveSuggestionsResolution> LoadOrganisationSuggestionsAsync(
        IHalleyApiClient apiClient,
        string token,
        CancellationToken cancellationToken)
    {
        var result = await apiClient.ListOrganisationsAsync(token, new ListOrganisationsQuery
        {
            Order = "name ASC",
            Size = 200
        }, cancellationToken);

        if (!result.IsSuccessStatusCode)
        {
            return InteractiveSuggestionsResolution.ApiError(result);
        }

        return InteractiveSuggestionsResolution.Success((result.JsonBody?["organisations"] as JsonArray)?
            .OfType<JsonObject>()
            .Select(organisation => new InteractiveSuggestion(
                organisation["name"]?.GetValue<string>() ?? string.Empty,
                organisation["active_license_hotline"]?.GetValue<bool>() == true ? "Hotline licensed" : "No Hotline license"))
            .Where(static suggestion => !string.IsNullOrWhiteSpace(suggestion.Value))
            .ToArray()
            ?? []);
    }

    private async Task<InteractiveSuggestionsResolution> LoadTemplateSuggestionsAsync(
        IHalleyApiClient apiClient,
        string token,
        int organisationId,
        CancellationToken cancellationToken)
    {
        var result = await apiClient.ListCallTemplatesAsync(token, new ListCallTemplatesQuery
        {
            ForOrganisationId = organisationId,
            Order = "name ASC",
            Size = 200
        }, cancellationToken);

        if (!result.IsSuccessStatusCode)
        {
            return InteractiveSuggestionsResolution.ApiError(result);
        }

        return InteractiveSuggestionsResolution.Success((result.JsonBody?["call_templates"] as JsonArray)?
            .OfType<JsonObject>()
            .Select(template => new InteractiveSuggestion(
                template["name"]?.GetValue<string>() ?? string.Empty,
                $"{template["uuid"]?.GetValue<string>()} (id {template["id"]?.GetValue<int>()})"))
            .Where(static suggestion => !string.IsNullOrWhiteSpace(suggestion.Value))
            .ToArray()
            ?? []);
    }

    private async Task<IReadOnlyList<InteractiveSuggestion>> LoadTemplateVersionSuggestionsAsync(
        IHalleyApiClient apiClient,
        string token,
        int organisationId,
        string templateReference,
        CancellationToken cancellationToken)
    {
        var resolution = await ResolveTemplateReferenceAsync(apiClient, token, organisationId, templateReference, null, cancellationToken);
        if (resolution.ErrorResult is not null || resolution.Template is null)
        {
            return [];
        }

        return resolution.AvailableVersions
            .Select(version => new InteractiveSuggestion(version.Id.ToString(CultureInfo.InvariantCulture), version.Description))
            .ToArray();
    }

    private async Task<OrganisationReferenceResolution> ResolveOrganisationReferenceAsync(
        IHalleyApiClient apiClient,
        string token,
        string organisationReference,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeOptionalText(organisationReference);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return OrganisationReferenceResolution.Error("A `--organisation` value is required.");
        }

        ApiCallResult result;
        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var organisationId) && organisationId > 0)
        {
            result = await apiClient.GetOrganisationAsync(token, organisationId, cancellationToken);
            if (!result.IsSuccessStatusCode)
            {
                return result.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? OrganisationReferenceResolution.Error($"Organisation `{normalized}` was not found.")
                    : OrganisationReferenceResolution.ApiError(result);
            }

            return ResolveOrganisationFromObject(result.JsonBody?["organisation"] as JsonObject, normalized);
        }

        result = await apiClient.ListOrganisationsAsync(token, new ListOrganisationsQuery
        {
            Name = normalized,
            Order = "name ASC",
            Size = 50
        }, cancellationToken);

        if (!result.IsSuccessStatusCode)
        {
            return OrganisationReferenceResolution.ApiError(result);
        }

        var organisations = (result.JsonBody?["organisations"] as JsonArray)?
            .OfType<JsonObject>()
            .ToArray()
            ?? [];

        var exactMatches = organisations
            .Where(organisation => string.Equals(
                organisation["name"]?.GetValue<string>(),
                normalized,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (exactMatches.Length == 1)
        {
            return ResolveOrganisationFromObject(exactMatches[0], normalized);
        }

        if (exactMatches.Length > 1)
        {
            return OrganisationReferenceResolution.Error($"Organisation name `{normalized}` is ambiguous. Use the organisation id instead.");
        }

        if (organisations.Length == 1)
        {
            return ResolveOrganisationFromObject(organisations[0], normalized);
        }

        return organisations.Length == 0
            ? OrganisationReferenceResolution.Error($"Organisation `{normalized}` was not found.")
            : OrganisationReferenceResolution.Error($"Organisation name `{normalized}` matched multiple results. Use the organisation id instead.");
    }

    private static OrganisationReferenceResolution ResolveOrganisationFromObject(JsonObject? organisation, string reference)
    {
        if (organisation is null)
        {
            return OrganisationReferenceResolution.Error($"Organisation `{reference}` could not be resolved.");
        }

        var id = organisation["id"]?.GetValue<int>();
        var name = organisation["name"]?.GetValue<string>();
        if (id is null || string.IsNullOrWhiteSpace(name))
        {
            return OrganisationReferenceResolution.Error($"Organisation `{reference}` returned an incomplete response.");
        }

        return OrganisationReferenceResolution.Success(new ResolvedOrganisation(
            reference,
            id.Value,
            name,
            organisation["active_license_hotline"]?.GetValue<bool>() == true));
    }

    private async Task<TemplateReferenceResolution> ResolveTemplateReferenceAsync(
        IHalleyApiClient apiClient,
        string token,
        int organisationId,
        string templateReference,
        int? templateId,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeOptionalText(templateReference);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return TemplateReferenceResolution.Error("A call template reference is required.");
        }

        if (templateId is not null)
        {
            var specificVersionResult = await apiClient.GetCallTemplateAsync(token, templateId.Value.ToString(CultureInfo.InvariantCulture), cancellationToken);
            if ((int)specificVersionResult.StatusCode == 404)
            {
                return TemplateReferenceResolution.Error($"Call template `{normalized}` does not have version id `{templateId.Value}`.");
            }

            if (!specificVersionResult.IsSuccessStatusCode)
            {
                return TemplateReferenceResolution.ApiError(specificVersionResult);
            }

            var specificTemplate = specificVersionResult.JsonBody?["call_template"] as JsonObject;
            if (specificTemplate is null)
            {
                return TemplateReferenceResolution.Error($"Call template `{normalized}` returned an incomplete response.");
            }

            if (!TemplateReferenceMatches(specificTemplate, normalized))
            {
                return TemplateReferenceResolution.Error($"Call template `{normalized}` does not have version id `{templateId.Value}`.");
            }

            return CreateTemplateReferenceResolution(specificTemplate, normalized);
        }

        var activeTemplateResult = await apiClient.GetCallTemplateAsync(token, normalized, cancellationToken);
        if (activeTemplateResult.IsSuccessStatusCode)
        {
            var activeTemplate = activeTemplateResult.JsonBody?["call_template"] as JsonObject;
            if (activeTemplate is null)
            {
                return TemplateReferenceResolution.Error($"Call template `{normalized}` returned an incomplete response.");
            }

            return CreateTemplateReferenceResolution(activeTemplate, normalized);
        }

        if ((int)activeTemplateResult.StatusCode != 404)
        {
            return TemplateReferenceResolution.ApiError(activeTemplateResult);
        }

        var byNameResult = await apiClient.ListCallTemplatesAsync(token, new ListCallTemplatesQuery
        {
            ForOrganisationId = organisationId,
            Order = "name ASC",
            Size = 200
        }, cancellationToken);

        if (!byNameResult.IsSuccessStatusCode)
        {
            return TemplateReferenceResolution.ApiError(byNameResult);
        }

        var nameMatches = (byNameResult.JsonBody?["call_templates"] as JsonArray)?
            .OfType<JsonObject>()
            .Where(template => string.Equals(template["name"]?.GetValue<string>(), normalized, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (nameMatches is null || nameMatches.Length == 0)
        {
            return TemplateReferenceResolution.Error($"Call template `{normalized}` was not found or is not available to organisation {organisationId}.");
        }

        if (nameMatches.Length > 1)
        {
            return TemplateReferenceResolution.Error($"Call template reference `{normalized}` matched multiple template groups. Use the template uuid instead.");
        }

        var resolvedUuid = nameMatches[0]["uuid"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(resolvedUuid))
        {
            return TemplateReferenceResolution.Error($"Call template `{normalized}` returned an incomplete response.");
        }

        var byUuidResult = await apiClient.GetCallTemplateAsync(token, resolvedUuid, cancellationToken);
        if (!byUuidResult.IsSuccessStatusCode)
        {
            return TemplateReferenceResolution.ApiError(byUuidResult);
        }

        var resolvedTemplate = byUuidResult.JsonBody?["call_template"] as JsonObject;
        if (resolvedTemplate is null)
        {
            return TemplateReferenceResolution.Error($"Call template `{normalized}` returned an incomplete response.");
        }

        return CreateTemplateReferenceResolution(resolvedTemplate, normalized);
    }

    private static CallRequestCreateRequest BuildCallRequest(CallCreateInput input) =>
        new()
        {
            CallRequest = new CallRequestWriteModel
            {
                OrganisationId = input.OrganisationId,
                Instructions = input.Instructions,
                Agenda = input.Agenda,
                ResultQuestions = input.Questions.Count == 0
                    ? null
                    : input.Questions.Select(question => new CallRequestQuestionWriteModel
                    {
                        Id = question.Id,
                        Text = question.Text,
                        Format = question.Format
                    }).ToArray(),
                CallNotes = input.Notes.Count == 0 ? null : input.Notes,
                HotlineCallTemplateUuid = input.TemplateUuid,
                HotlineCallTemplateId = input.TemplateId,
                CallData = new CallRequestDataWriteModel
                {
                    CallMethod = input.CallMethod,
                    PhoneNumber = input.PhoneNumber,
                    RecipientName = input.RecipientName,
                    RecipientTimezone = input.RecipientTimezone
                }
            }
        };

    private static TemplateReferenceResolution CreateTemplateReferenceResolution(JsonObject selectedTemplate, string reference)
    {
        var matchingVersions = (selectedTemplate["versions"] as JsonArray)?
            .OfType<JsonObject>()
            .Select(template => new TemplateVersionInfo(
                template["id"]?.GetValue<int>() ?? 0,
                $"version {template["version"]?.GetValue<int>() ?? 0}{(template["active"]?.GetValue<bool>() == true ? " (active)" : string.Empty)}"))
            .Where(version => version.Id > 0)
            .OrderByDescending(version => version.Id)
            .ToArray()
            ?? [];

        var uuid = selectedTemplate["uuid"]?.GetValue<string>();
        var id = selectedTemplate["id"]?.GetValue<int>();
        var name = selectedTemplate["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(uuid) || id is null || string.IsNullOrWhiteSpace(name))
        {
            return TemplateReferenceResolution.Error($"Call template `{reference}` returned an incomplete response.");
        }

        return TemplateReferenceResolution.Success(new ValidatedTemplateReference(
            uuid,
            id,
            name), matchingVersions);
    }

    private static bool TemplateReferenceMatches(JsonObject template, string reference) =>
        string.Equals(template["uuid"]?.GetValue<string>(), reference, StringComparison.OrdinalIgnoreCase)
        || string.Equals(template["name"]?.GetValue<string>(), reference, StringComparison.OrdinalIgnoreCase)
        || string.Equals(template["id"]?.GetValue<int>().ToString(CultureInfo.InvariantCulture), reference, StringComparison.OrdinalIgnoreCase);

    private async Task WriteCallReplayHintAsync(
        ParseResult parseResult,
        CallCreateInput input,
        CancellationToken cancellationToken)
    {
        var command = BuildCallReplayCommand(parseResult, input);
        await _stderr.WriteLineAsync($"To make this call again execute: {command}".AsMemory(), cancellationToken);
    }

    private CommandOutput BuildCallCommandOutput(ParseResult parseResult, CallCreateInput input)
    {
        var command = BuildCallReplayCommand(parseResult, input);
        return CommandOutput.Json(
            new JsonObject { ["command"] = command },
            JsonValue.Create(command)!);
    }

    private string BuildCallReplayCommand(ParseResult parseResult, CallCreateInput input)
    {
        var arguments = new List<string>
        {
            GetReplayCommandName(),
            "calls",
            "create",
            "--organisation-id",
            input.OrganisationId.ToString(CultureInfo.InvariantCulture),
            "--call-method",
            input.CallMethod,
            "--recipient-name",
            input.RecipientName,
            "--recipient-timezone",
            input.RecipientTimezone
        };

        if (!string.IsNullOrWhiteSpace(input.PhoneNumber))
        {
            arguments.Add("--phone-number");
            arguments.Add(input.PhoneNumber);
        }

        if (!string.IsNullOrWhiteSpace(input.TemplateUuid))
        {
            arguments.Add("--template-uuid");
            arguments.Add(input.TemplateUuid);
        }

        if (input.TemplateId is not null)
        {
            arguments.Add("--template-id");
            arguments.Add(input.TemplateId.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(input.Instructions))
        {
            arguments.Add("--instructions");
            arguments.Add(input.Instructions);
        }

        if (!string.IsNullOrWhiteSpace(input.Agenda))
        {
            arguments.Add("--agenda");
            arguments.Add(input.Agenda);
        }

        foreach (var note in input.Notes)
        {
            arguments.Add("--note");
            arguments.Add(note);
        }

        foreach (var question in input.Questions)
        {
            arguments.Add("--question");
            arguments.Add($"{question.Id}:{question.Format}:{question.Text}");
        }

        var endpoint = parseResult.GetValue(_endpointOption);
        if (WasOptionProvided(parseResult, _endpointOption)
            && !string.Equals(
                HalleyEndpointResolver.Resolve(endpoint).SessionKey,
                new HalleyApiClientOptions().SessionKey,
                StringComparison.Ordinal))
        {
            arguments.Add("--endpoint");
            arguments.Add(endpoint!);
        }

        return string.Join(" ", arguments.Select(FormatReplayCommandArgument));
    }

    private string GetReplayCommandName()
    {
        var commandName = NormalizeOptionalText(_replayCommandNameProvider());
        return string.IsNullOrWhiteSpace(commandName) ? "halley-cli" : commandName;
    }

    private string FormatReplayCommandArgument(string value) =>
        _isWindowsProvider()
            ? FormatWindowsCommandArgument(value)
            : FormatPosixShellArgument(value);

    private static string FormatPosixShellArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "''";
        }

        if (value.All(IsShellSafeCharacter))
        {
            return value;
        }

        return value.Any(static ch => ch is '\n' or '\r' or '\t')
            ? "$'" + EscapeAnsiCString(value) + "'"
            : "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    private static string FormatWindowsCommandArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        if (value.All(IsWindowsCommandSafeCharacter))
        {
            return value;
        }

        var builder = new StringBuilder();
        builder.Append('"');
        var trailingBackslashCount = 0;

        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                trailingBackslashCount++;
                continue;
            }

            if (ch == '"')
            {
                builder.Append('\\', trailingBackslashCount * 2 + 1);
                builder.Append('"');
                trailingBackslashCount = 0;
                continue;
            }

            if (trailingBackslashCount > 0)
            {
                builder.Append('\\', trailingBackslashCount);
                trailingBackslashCount = 0;
            }

            builder.Append(ch);
        }

        if (trailingBackslashCount > 0)
        {
            builder.Append('\\', trailingBackslashCount * 2);
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static bool IsShellSafeCharacter(char ch) =>
        char.IsAsciiLetterOrDigit(ch)
        || ch is '-' or '_' or '.' or '/' or ':' or '+' or '=' or '@' or ',';

    private static bool IsWindowsCommandSafeCharacter(char ch) =>
        char.IsAsciiLetterOrDigit(ch)
        || ch is '-' or '_' or '.' or '/' or '\\' or ':' or '+' or '=' or '@' or ',';

    private static string EscapeAnsiCString(string value)
    {
        var builder = new StringBuilder();
        foreach (var ch in value)
        {
            builder.Append(ch switch
            {
                '\\' => "\\\\",
                '\'' => "\\'",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => ch.ToString()
            });
        }

        return builder.ToString();
    }

    private static string DefaultReplayCommandNameProvider()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var fileName = Path.GetFileName(processPath);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }

        return "halley-cli";
    }

    private CallWaitConfigurationResolution GetCallWaitConfiguration(ParseResult parseResult, CallExecutionOptions options)
    {
        if (!TryParseDuration(parseResult.GetValue(options.PollEveryOption), TimeSpan.FromSeconds(5), out var pollEvery, out var pollEveryError))
        {
            return CallWaitConfigurationResolution.Error($"Invalid --poll-every value. {pollEveryError}");
        }

        if (pollEvery is null || pollEvery <= TimeSpan.Zero)
        {
            return CallWaitConfigurationResolution.Error("Invalid --poll-every value. The duration must be greater than zero.");
        }

        if (!TryParseDuration(parseResult.GetValue(options.TimeoutOption), null, out var timeout, out var timeoutError))
        {
            return CallWaitConfigurationResolution.Error($"Invalid --timeout value. {timeoutError}");
        }

        if (timeout is { } timeoutValue && timeoutValue <= TimeSpan.Zero)
        {
            return CallWaitConfigurationResolution.Error("Invalid --timeout value. The duration must be greater than zero.");
        }

        return CallWaitConfigurationResolution.Success(new CallWaitConfiguration(pollEvery.Value, timeout));
    }

    private async Task<CallStatusResolution> LoadCallStatusAsync(
        IHalleyApiClient apiClient,
        string token,
        string callRequestUuid,
        bool waitForResult,
        CallWaitConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var startedAt = _clock.UtcNow;

        while (true)
        {
            var status = await FetchCallStatusAsync(apiClient, token, callRequestUuid, waitForResult, timedOut: false, cancellationToken);
            if (status.ErrorResult is not null || status.ErrorMessage is not null)
            {
                return status;
            }

            if (!waitForResult || status.HasResult)
            {
                return status;
            }

            if (configuration.Timeout is not null && _clock.UtcNow - startedAt >= configuration.Timeout.Value)
            {
                var timedOutStatus = await FetchCallStatusAsync(apiClient, token, callRequestUuid, waited: true, timedOut: true, cancellationToken);
                return timedOutStatus with { TimedOut = true };
            }

            CurrentLogger.Information("Waiting {PollEvery} for a result for call request {CallRequestUuid}.", configuration.PollEvery, callRequestUuid);
            await _clock.DelayAsync(configuration.PollEvery, cancellationToken);
        }
    }

    private async Task<CallStatusResolution> FetchCallStatusAsync(
        IHalleyApiClient apiClient,
        string token,
        string callRequestUuid,
        bool waited,
        bool timedOut,
        CancellationToken cancellationToken)
    {
        var callRequestTask = apiClient.GetCallRequestAsync(token, callRequestUuid, cancellationToken);
        var callResultsTask = apiClient.ListCallResultsForRequestAsync(token, callRequestUuid, cancellationToken);
        await Task.WhenAll(callRequestTask, callResultsTask);

        var callRequestResult = await callRequestTask;
        if (!callRequestResult.IsSuccessStatusCode)
        {
            return CallStatusResolution.ApiError(callRequestResult);
        }

        var callResultsResult = await callResultsTask;
        if (!callResultsResult.IsSuccessStatusCode)
        {
            return CallStatusResolution.ApiError(callResultsResult);
        }

        if (callRequestResult.JsonBody?["call_request"] is not JsonObject callRequest)
        {
            return CallStatusResolution.Error("The call request response did not contain a `call_request` object.");
        }

        var callResults = GetOrderedCallResults(callResultsResult.JsonBody);
        var payload = BuildCallStatusPayload(callRequest, callResults, waited, timedOut);
        var humanPayload = BuildCallStatusHumanPayload(payload);
        return CallStatusResolution.Success(payload, humanPayload, timedOut, callResults.Count > 0);
    }

    private static JsonObject BuildCallStatusPayload(JsonObject callRequest, JsonArray callResults, bool waited, bool timedOut)
    {
        var requestStatus = callRequest["status"]?.GetValue<string>();
        var state = DetermineCallState(requestStatus, callResults.Count);
        var latestResult = callResults.Count == 0 ? null : callResults[0]?.DeepClone();

        return new JsonObject
        {
            ["call_request_uuid"] = callRequest["uuid"]?.DeepClone(),
            ["state"] = state,
            ["request_status"] = requestStatus,
            ["result_count"] = callResults.Count,
            ["latest_call_result"] = latestResult,
            ["call_request"] = callRequest.DeepClone(),
            ["call_results"] = callResults.DeepClone(),
            ["waited"] = waited,
            ["timed_out"] = timedOut
        };
    }

    private static JsonObject BuildCallStatusHumanPayload(JsonObject payload)
    {
        var callRequest = payload["call_request"] as JsonObject;
        var callData = callRequest?["call_data"] as JsonObject;
        var latestResult = payload["latest_call_result"] as JsonObject;

        return new JsonObject
        {
            ["call_request_uuid"] = payload["call_request_uuid"]?.DeepClone(),
            ["state"] = payload["state"]?.DeepClone(),
            ["request_status"] = payload["request_status"]?.DeepClone(),
            ["result_count"] = payload["result_count"]?.DeepClone(),
            ["organisation_id"] = callRequest?["organisation_id"]?.DeepClone(),
            ["created_at"] = callRequest?["created_at"]?.DeepClone(),
            ["call_method"] = callData?["call_method"]?.DeepClone(),
            ["recipient_name"] = callData?["recipient_name"]?.DeepClone(),
            ["recipient_timezone"] = callData?["recipient_timezone"]?.DeepClone(),
            ["phone_number"] = callData?["phone_number"]?.DeepClone(),
            ["latest_result_uuid"] = latestResult?["uuid"]?.DeepClone(),
            ["latest_result_status"] = latestResult?["status"]?.DeepClone(),
            ["latest_result"] = latestResult?["result"]?.DeepClone(),
            ["latest_result_type"] = latestResult?["result_type"]?.DeepClone(),
            ["latest_result_answered_at"] = latestResult?["answered_at"]?.DeepClone(),
            ["timed_out"] = payload["timed_out"]?.DeepClone(),
            ["waited"] = payload["waited"]?.DeepClone()
        };
    }

    private async Task<DeleteCallResultResolution> DeleteLatestCallResultIfRequestedAsync(
        IHalleyApiClient apiClient,
        string token,
        string callRequestUuid,
        CallStatusResolution status,
        OutputMode outputMode,
        CancellationToken cancellationToken)
    {
        _ = outputMode;

        var latestCallResult = status.JsonPayload?["latest_call_result"] as JsonObject;
        var callResultUuid = latestCallResult?["uuid"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(callResultUuid))
        {
            return DeleteCallResultResolution.Warning($"No call result was available to delete for call request `{callRequestUuid}`.");
        }

        CurrentLogger.Information("Deleting call result {CallResultUuid} for call request {CallRequestUuid}.", callResultUuid, callRequestUuid);
        var deleteResult = await apiClient.DeleteCallResultAsync(token, callResultUuid, cancellationToken);
        if (!deleteResult.IsSuccessStatusCode)
        {
            return DeleteCallResultResolution.ApiError(deleteResult);
        }

        return DeleteCallResultResolution.Success(callResultUuid);
    }

    private static CallStatusResolution ApplyDeleteOutcome(CallStatusResolution status, DeleteCallResultResolution deleteResult)
    {
        var deletedCallResultUuid = deleteResult.DeletedCallResultUuid;

        if (status.JsonPayload is null || status.HumanPayload is null)
        {
            return status;
        }

        var jsonPayload = (JsonObject)status.JsonPayload.DeepClone();
        jsonPayload["delete_requested"] = true;
        jsonPayload["deleted_call_result"] = deletedCallResultUuid is not null;
        jsonPayload["deleted_call_result_uuid"] = deletedCallResultUuid;

        var humanPayload = (JsonObject)status.HumanPayload.DeepClone();
        humanPayload["delete_requested"] = true;
        humanPayload["deleted_call_result"] = deletedCallResultUuid is not null;
        humanPayload["deleted_call_result_uuid"] = deletedCallResultUuid;

        return status with
        {
            JsonPayload = jsonPayload,
            HumanPayload = humanPayload
        };
    }

    private static JsonObject BuildCallResultsHumanPayload(JsonNode rawPayload)
    {
        var orderedResults = GetOrderedCallResults(rawPayload);
        var rows = new JsonArray();
        foreach (var result in orderedResults)
        {
            if (result is not JsonObject resultObject)
            {
                continue;
            }

            rows.Add(new JsonObject
            {
                ["uuid"] = resultObject["uuid"]?.DeepClone(),
                ["created_at"] = resultObject["created_at"]?.DeepClone(),
                ["answered_at"] = resultObject["answered_at"]?.DeepClone(),
                ["status"] = resultObject["status"]?.DeepClone(),
                ["result"] = resultObject["result"]?.DeepClone(),
                ["result_type"] = resultObject["result_type"]?.DeepClone()
            });
        }

        return new JsonObject
        {
            ["call_results"] = rows
        };
    }

    private static JsonArray GetOrderedCallResults(JsonNode? rawPayload)
    {
        var source = rawPayload?["call_results"] as JsonArray;
        if (source is null)
        {
            return [];
        }

        return new JsonArray(
            source
                .OrderByDescending(item => ParseDateTimeOffset(item?["created_at"]?.GetValue<string>()))
                .Select(item => item?.DeepClone())
                .ToArray());
    }

    private static string DetermineCallState(string? requestStatus, int resultCount) =>
        resultCount > 0 || string.Equals(requestStatus, "completed", StringComparison.OrdinalIgnoreCase) ? "completed" :
        string.Equals(requestStatus, "pending", StringComparison.OrdinalIgnoreCase) ? "queued" :
        string.Equals(requestStatus, "active", StringComparison.OrdinalIgnoreCase) ? "running" :
        "unknown";

    private static IReadOnlyList<InteractiveSuggestion> GetTimezoneSuggestions() =>
    [
        new("Australia/Melbourne", "Example Australia timezone."),
        new("Australia/Sydney", "Another Australia timezone."),
        new("Europe/London", "Example Europe timezone."),
        new("America/New_York", "Example North America timezone.")
    ];

    private async Task<string?> PromptForRequiredLineAsync(
        string prompt,
        CancellationToken cancellationToken,
        IReadOnlyList<InteractiveSuggestion>? suggestions = null,
        string? helpText = null,
        Func<string, string?>? validator = null)
    {
        while (true)
        {
            var value = await _interactiveUi.ReadLineAsync(_stderr, prompt, suggestions, helpText, cancellationToken);
            if (value is null)
            {
                return null;
            }

            value = NormalizeOptionalText(value);
            if (!string.IsNullOrWhiteSpace(value))
            {
                if (validator is not null && validator(value) is { } validationError)
                {
                    await _stderr.WriteLineAsync(validationError.AsMemory(), cancellationToken);
                    continue;
                }

                return value;
            }

            await _stderr.WriteLineAsync("A value is required.".AsMemory(), cancellationToken);
        }
    }

    private async Task<string?> PromptForChoiceAsync(
        string prompt,
        Func<string, string?> parser,
        string invalidMessage,
        IReadOnlyList<InteractiveSuggestion>? suggestions,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var value = await _interactiveUi.ReadLineAsync(_stderr, prompt, suggestions, invalidMessage, cancellationToken);
            if (value is null)
            {
                return null;
            }

            var parsed = parser(NormalizeOptionalText(value)?.ToLowerInvariant() ?? string.Empty);
            if (parsed is not null)
            {
                return parsed;
            }

            await _stderr.WriteLineAsync(invalidMessage.AsMemory(), cancellationToken);
        }
    }

    private async Task<int?> PromptForPositiveIntAsync(
        string prompt,
        CancellationToken cancellationToken,
        IReadOnlyList<InteractiveSuggestion>? suggestions = null,
        string? helpText = null)
    {
        while (true)
        {
            var value = await _interactiveUi.ReadLineAsync(_stderr, prompt, suggestions, helpText, cancellationToken);
            if (value is null)
            {
                return null;
            }

            if (int.TryParse(NormalizeOptionalText(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            {
                return parsed;
            }

            await _stderr.WriteLineAsync("Enter a positive integer.".AsMemory(), cancellationToken);
        }
    }

    private async Task<int?> PromptForOptionalIntAsync(
        string prompt,
        CancellationToken cancellationToken,
        IReadOnlyList<InteractiveSuggestion>? suggestions = null,
        string? helpText = null)
    {
        while (true)
        {
            var value = await _interactiveUi.ReadLineAsync(_stderr, prompt, suggestions, helpText, cancellationToken);
            if (value is null)
            {
                return CallPromptSentinel.CancelledInt;
            }

            value = NormalizeOptionalText(value);
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            {
                return parsed;
            }

            await _stderr.WriteLineAsync("Enter a positive integer or leave the value blank.".AsMemory(), cancellationToken);
        }
    }

    private async Task<bool?> PromptForYesNoAsync(
        string prompt,
        CancellationToken cancellationToken,
        IReadOnlyList<InteractiveSuggestion>? suggestions = null,
        string? helpText = null)
    {
        while (true)
        {
            var value = await _interactiveUi.ReadLineAsync(_stderr, prompt, suggestions ?? YesNoSuggestions, helpText ?? "Enter `yes` or `no`.", cancellationToken);
            if (value is null)
            {
                return null;
            }

            value = NormalizeOptionalText(value)?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(value) || value is "n" or "no")
            {
                return false;
            }

            if (value is "y" or "yes")
            {
                return true;
            }

            await _stderr.WriteLineAsync("Enter `y` or `n`.".AsMemory(), cancellationToken);
        }
    }

    private static async Task<string?> ResolveTextInputAsync(string? inlineValue, string? filePath, CancellationToken cancellationToken)
    {
        var inline = NormalizeOptionalText(inlineValue);
        if (!string.IsNullOrWhiteSpace(inline))
        {
            return inline;
        }

        var path = NormalizeOptionalText(filePath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken);
        return NormalizeOptionalText(content);
    }

    private static bool TryParseCallQuestions(
        IReadOnlyList<string> tokens,
        out IReadOnlyList<CallQuestionInput> questions,
        out string? error)
    {
        var parsedQuestions = new List<CallQuestionInput>();
        foreach (var token in tokens.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (!TryParseCallQuestion(token, out var question, out error))
            {
                questions = [];
                return false;
            }

            parsedQuestions.Add(question!);
        }

        questions = parsedQuestions;
        error = null;
        return true;
    }

    private static bool TryParseCallQuestion(string token, out CallQuestionInput? question, out string? error)
    {
        question = null;
        error = null;

        var firstSeparator = token.IndexOf(':');
        var secondSeparator = firstSeparator < 0 ? -1 : token.IndexOf(':', firstSeparator + 1);
        if (firstSeparator <= 0 || secondSeparator <= firstSeparator + 1 || secondSeparator >= token.Length - 1)
        {
            error = $"Invalid --question value `{token}`. Expected `id:format:text`.";
            return false;
        }

        var idText = token[..firstSeparator];
        var format = token[(firstSeparator + 1)..secondSeparator];
        var text = token[(secondSeparator + 1)..];

        if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            error = $"Invalid --question value `{token}`. Question ids must be positive integers.";
            return false;
        }

        format = NormalizeOptionalText(format)?.ToLowerInvariant() ?? string.Empty;
        if (format is not "string" and not "boolean")
        {
            error = $"Invalid --question value `{token}`. The format must be `string` or `boolean`.";
            return false;
        }

        text = NormalizeOptionalText(text) ?? string.Empty;
        if (text.Length == 0)
        {
            error = $"Invalid --question value `{token}`. Question text is required.";
            return false;
        }

        question = new CallQuestionInput(id, text, format);
        return true;
    }

    private static bool TryParseDuration(string? value, TimeSpan? defaultValue, out TimeSpan? parsed, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parsed = defaultValue;
            error = null;
            return true;
        }

        var trimmed = value.Trim();
        if (TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out var timeSpan))
        {
            parsed = timeSpan;
            error = null;
            return true;
        }

        if (trimmed.Length > 1)
        {
            var suffix = char.ToLowerInvariant(trimmed[^1]);
            var numberPart = trimmed[..^1];
            if (double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            {
                parsed = suffix switch
                {
                    's' => TimeSpan.FromSeconds(amount),
                    'm' => TimeSpan.FromMinutes(amount),
                    'h' => TimeSpan.FromHours(amount),
                    _ => TimeSpan.Zero
                };

                if (parsed != TimeSpan.Zero || amount == 0)
                {
                    error = null;
                    return suffix is 's' or 'm' or 'h';
                }
            }
        }

        parsed = null;
        error = "Expected values like `5s`, `1m`, `2h`, or `00:00:05`.";
        return false;
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<int> HandleLoginResultAsync(ApiCallResult result, string authType, ParseResult parseResult, OutputMode outputMode, CancellationToken cancellationToken)
    {
        if (!result.IsSuccessStatusCode)
        {
            return await WriteApiErrorAsync(result, outputMode, cancellationToken);
        }

        var token = result.JsonBody?["token"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(token))
        {
            return await WriteCliErrorAsync("The login response did not contain a token.", outputMode, cancellationToken);
        }

        try
        {
            var sessionEndpointKey = ResolveSessionEndpointKey(parseResult);
            await _sessionStore.SaveAsync(sessionEndpointKey, new SessionRecord(token, authType, _clock.Now), cancellationToken);
            CurrentLogger.Information("Saved {AuthType} session token for {Endpoint} to {SessionPath}.", authType, sessionEndpointKey, _sessionStore.SessionPath);
        }
        catch (Exception ex)
        {
            CurrentLogger.Error(ex, "Failed to save session to {SessionPath}.", _sessionStore.SessionPath);
            return await WriteCliErrorAsync($"Failed to save session to {_sessionStore.SessionPath}: {ex.Message}", outputMode, cancellationToken);
        }

        return await WriteSuccessAsync(CommandOutput.TokenValue(token), outputMode, cancellationToken);
    }

    private async Task<int> HandleApiResultAsync(ApiCallResult result, OutputMode outputMode, CancellationToken cancellationToken)
    {
        if (!result.IsSuccessStatusCode)
        {
            return await WriteApiErrorAsync(result, outputMode, cancellationToken);
        }

        if (result.StatusCode == System.Net.HttpStatusCode.NoContent || result.JsonBody is null)
        {
            return await WriteSuccessAsync(CommandOutput.Empty(), outputMode, cancellationToken);
        }

        return await WriteSuccessAsync(CommandOutput.Json(result.JsonBody), outputMode, cancellationToken);
    }

    private async Task<string?> RequireTokenAsync(ParseResult parseResult, OutputMode outputMode, CancellationToken cancellationToken)
    {
        var explicitToken = parseResult.GetValue(_tokenOption);
        if (!string.IsNullOrWhiteSpace(explicitToken))
        {
            CurrentLogger.Debug("Using the token supplied via --token.");
            if (!await ValidateTokenForUseAsync(
                    explicitToken,
                    "The supplied JWT has expired. Run `login ...` again or pass a fresh `--token`.",
                    outputMode,
                    cancellationToken))
            {
                return null;
            }

            return explicitToken;
        }

        try
        {
            var sessionEndpointKey = ResolveSessionEndpointKey(parseResult);
            var session = await _sessionStore.LoadAsync(sessionEndpointKey, cancellationToken);
            if (!string.IsNullOrWhiteSpace(session?.Token))
            {
                if (!await ValidateTokenForUseAsync(
                        session.Token,
                        $"The saved session token for `{sessionEndpointKey}` has expired. Run `login ...` again for that endpoint or pass a fresh `--token`.",
                        outputMode,
                        cancellationToken))
                {
                    return null;
                }

                CurrentLogger.Information("Loaded a saved session token for {Endpoint} from {SessionPath}.", sessionEndpointKey, _sessionStore.SessionPath);
                return session.Token;
            }

            CurrentLogger.Warning("No saved session token was found for {Endpoint} at {SessionPath}.", sessionEndpointKey, _sessionStore.SessionPath);
            await WriteCliErrorAsync($"No saved session token was found for `{sessionEndpointKey}`. Run `login ...` for that endpoint first or pass `--token`.", outputMode, cancellationToken);
            return null;
        }
        catch (Exception ex)
        {
            CurrentLogger.Error(ex, "Failed to read session from {SessionPath}.", _sessionStore.SessionPath);
            await WriteCliErrorAsync($"Failed to read session from {_sessionStore.SessionPath}: {ex.Message}", outputMode, cancellationToken);
            return null;
        }
    }

    private async Task<bool> ValidateTokenForUseAsync(
        string token,
        string expiredMessage,
        OutputMode outputMode,
        CancellationToken cancellationToken)
    {
        if (!JwtTokenInspector.TryGetExpirationUtc(token, out var expiresAtUtc) || expiresAtUtc is null)
        {
            return true;
        }

        if (expiresAtUtc.Value > _clock.UtcNow)
        {
            return true;
        }

        CurrentLogger.Warning("Rejected expired JWT that expired at {ExpiresAtUtc}.", expiresAtUtc.Value);
        await WriteCliErrorAsync($"{expiredMessage} Expired at {expiresAtUtc.Value.UtcDateTime:u}.", outputMode, cancellationToken);
        return false;
    }

    private ApiClientResolution GetApiClient(ParseResult parseResult)
    {
        try
        {
            var endpoint = parseResult.GetValue(_endpointOption);
            var options = HalleyEndpointResolver.Resolve(endpoint);
            CurrentLogger.Debug("Resolved endpoint {EndpointInput} to auth {AuthBaseUri} and api {ApiBaseUri}.", endpoint, options.AuthBaseUri, options.ApiBaseUri);
            return ApiClientResolution.Success(_apiClientFactory(options, CurrentLogger));
        }
        catch (ArgumentException ex)
        {
            CurrentLogger.Warning("Endpoint resolution failed: {Message}", ex.Message);
            return ApiClientResolution.Error(ex.Message);
        }
    }

    private string ResolveSessionEndpointKey(ParseResult parseResult)
    {
        var endpoint = parseResult.GetValue(_endpointOption);
        return HalleyEndpointResolver.Resolve(endpoint).SessionKey;
    }

    private async Task<int> WriteSuccessAsync(CommandOutput output, OutputMode outputMode, CancellationToken cancellationToken)
    {
        var text = _formatter.FormatSuccess(output, outputMode);
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        await _stdout.WriteLineAsync(text.AsMemory(), cancellationToken);
        return 0;
    }

    private async Task<int> WriteApiErrorAsync(ApiCallResult result, OutputMode outputMode, CancellationToken cancellationToken)
    {
        var text = _formatter.FormatApiError(result, outputMode);
        await _stderr.WriteLineAsync(text.AsMemory(), cancellationToken);
        return 1;
    }

    private async Task<int> WriteCliErrorAsync(string message, OutputMode outputMode, CancellationToken cancellationToken)
    {
        var text = _formatter.FormatCliError(message, outputMode);
        await _stderr.WriteLineAsync(text.AsMemory(), cancellationToken);
        return 1;
    }

    private async Task WriteCliWarningAsync(string message, OutputMode outputMode, CancellationToken cancellationToken)
    {
        CurrentLogger.Warning("{WarningMessage}", message);

        var text = outputMode == OutputMode.Json
            ? new JsonObject { ["warning"] = message }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
            : $"Warning: {message}";

        await _stderr.WriteLineAsync(text.AsMemory(), cancellationToken);
    }

    private OutputMode GetOutputMode(ParseResult parseResult) =>
        string.Equals(parseResult.GetValue(_outputOption), "json", StringComparison.OrdinalIgnoreCase)
            ? OutputMode.Json
            : OutputMode.Human;

    private ILogger CurrentLogger => _currentLogger.Value ?? SilentLogger;

    private bool TryResolveLogConfiguration(
        string[] args,
        ParseResult parseResult,
        out HalleyLogConfiguration configuration,
        out string? error)
    {
        error = null;

        var providedValue = parseResult.GetValue(_logOption);
        var wasProvided = WasOptionProvided(args, "--log");
        if (!wasProvided)
        {
            configuration = HalleyLogConfiguration.Enabled(LogEventLevel.Warning);
            return true;
        }

        if (string.IsNullOrWhiteSpace(providedValue))
        {
            configuration = HalleyLogConfiguration.Enabled(LogEventLevel.Information);
            return true;
        }

        switch (providedValue.Trim().ToLowerInvariant())
        {
            case "trace":
            case "verbose":
                configuration = HalleyLogConfiguration.Enabled(LogEventLevel.Verbose);
                return true;

            case "debug":
                configuration = HalleyLogConfiguration.Enabled(LogEventLevel.Debug);
                return true;

            case "info":
            case "information":
                configuration = HalleyLogConfiguration.Enabled(LogEventLevel.Information);
                return true;

            case "warn":
            case "warning":
                configuration = HalleyLogConfiguration.Enabled(LogEventLevel.Warning);
                return true;

            case "error":
                configuration = HalleyLogConfiguration.Enabled(LogEventLevel.Error);
                return true;

            case "fatal":
                configuration = HalleyLogConfiguration.Enabled(LogEventLevel.Fatal);
                return true;

            case "none":
                configuration = HalleyLogConfiguration.Disabled();
                return true;

            default:
                configuration = HalleyLogConfiguration.Enabled(LogEventLevel.Warning);
                error = $"Invalid --log level `{providedValue}`. Expected one of: trace, debug, info, warning, error, fatal, none.";
                return false;
        }
    }

    private bool IsInteractiveConsole() =>
        ReferenceEquals(_stderr, Console.Error) && !Console.IsErrorRedirected;

    private static bool WasOptionProvided(string[] args, string optionName) =>
        args.Any(argument => string.Equals(argument, optionName, StringComparison.Ordinal) || argument.StartsWith($"{optionName}=", StringComparison.Ordinal));

    private static bool WasOptionProvided<T>(ParseResult parseResult, Option<T> option) =>
        parseResult.GetResult(option) is { Implicit: false };

    private static string[] SanitizeArguments(string[] args)
    {
        var sensitiveOptions = new HashSet<string>(StringComparer.Ordinal)
        {
            "--password",
            "--secret",
            "--token"
        };

        var sanitized = new string[args.Length];
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (index > 0 && sensitiveOptions.Contains(args[index - 1]))
            {
                sanitized[index] = "[redacted]";
                continue;
            }

            var separatorIndex = argument.IndexOf('=');
            if (separatorIndex > 0)
            {
                var optionNameOnly = argument[..separatorIndex];
                if (sensitiveOptions.Contains(optionNameOnly))
                {
                    sanitized[index] = $"{optionNameOnly}=[redacted]";
                    continue;
                }
            }

            sanitized[index] = argument;
        }

        return sanitized;
    }

    private static bool TryParseDateTimeOffset(string? value, out DateTimeOffset? parsed)
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (DateTimeOffset.TryParse(value, out var dateTimeOffset))
        {
            parsed = dateTimeOffset;
            return true;
        }

        return false;
    }

    private static bool IsCountryCode(string value) =>
        value.Length == 2 && value.All(character => character is >= 'A' and <= 'Z');

    private static Option<T> CreateOption<T>(string name, string description) =>
        new(name) { Description = description };

    private static Option<T> CreateRequiredOption<T>(string name, string description) =>
        new(name) { Description = description, Required = true };

    private sealed record OrganisationWriteOptions(
        Option<string> NameOption,
        Option<string[]> FacilityOption,
        Option<string?> NotificationTemplateOption,
        Option<string[]> AuthIpOption,
        Option<string?> AdminUserNameOption,
        Option<bool?> CreateDefaultResourcesOption,
        Option<string?> AlertTitleTemplateOption,
        Option<bool?> PersonalOrganisationOption);

    private sealed record UserWriteOptions(
        Option<string> NameOption,
        Option<string> PasswordOption,
        Option<string?> CountryOption,
        Option<int?> ContactIdOption,
        Option<string?> ContactNameOption,
        Option<string?> ContactEmailOption,
        Option<string?> ContactPhoneOption);

    private sealed record CallCreateOptions(
        Option<string?> OrganisationOption,
        Option<string?> CallMethodOption,
        Option<string?> PhoneNumberOption,
        Option<string?> RecipientNameOption,
        Option<string?> RecipientTimezoneOption,
        Option<string?> TemplateUuidOption,
        Option<int?> TemplateIdOption,
        Option<string?> InstructionsOption,
        Option<string?> InstructionsFileOption,
        Option<string?> AgendaOption,
        Option<string?> AgendaFileOption,
        Option<string[]> NoteOption,
        Option<string[]> QuestionOption);

    private sealed record CallExecutionOptions(
        Option<bool> WaitOption,
        Option<bool> DeleteOption,
        Option<string?> PollEveryOption,
        Option<string?> TimeoutOption);

    private sealed record CallCreateCandidate(
        string? OrganisationReference,
        string? CallMethod,
        string? PhoneNumber,
        string? RecipientName,
        string? RecipientTimezone,
        string? TemplateReference,
        int? TemplateId,
        string? Instructions,
        string? Agenda,
        IReadOnlyList<string> Notes,
        IReadOnlyList<CallQuestionInput> Questions);

    private sealed record CallCreateInput(
        int OrganisationId,
        string OrganisationName,
        string CallMethod,
        string? PhoneNumber,
        string RecipientName,
        string RecipientTimezone,
        string? TemplateUuid,
        int? TemplateId,
        string? Instructions,
        string? Agenda,
        IReadOnlyList<string> Notes,
        IReadOnlyList<CallQuestionInput> Questions);

    private sealed record CallQuestionInput(int Id, string Text, string Format);

    private sealed record CallCreateCandidateResolution(CallCreateCandidate? Candidate, string? ErrorMessage, ApiCallResult? ErrorResult, bool ShouldReturnCommandOnly)
    {
        public static CallCreateCandidateResolution Success(CallCreateCandidate candidate, bool shouldReturnCommandOnly = false) => new(candidate, null, null, shouldReturnCommandOnly);

        public static CallCreateCandidateResolution Error(string errorMessage) => new(null, errorMessage, null, false);

        public static CallCreateCandidateResolution ApiError(ApiCallResult errorResult) => new(null, null, errorResult, false);
    }

    private sealed record CallCreateInputResolution(CallCreateInput? Input, string? ErrorMessage, ApiCallResult? ErrorResult, bool ShouldWriteReplayHint, bool ShouldReturnCommandOnly)
    {
        public static CallCreateInputResolution Success(CallCreateInput input, bool shouldWriteReplayHint = false, bool shouldReturnCommandOnly = false) => new(input, null, null, shouldWriteReplayHint, shouldReturnCommandOnly);

        public static CallCreateInputResolution Error(string errorMessage) => new(null, errorMessage, null, false, false);

        public static CallCreateInputResolution ApiError(ApiCallResult errorResult) => new(null, null, errorResult, false, false);
    }

    private sealed record CallNotesResolution(IReadOnlyList<string> Notes, string? ErrorMessage)
    {
        public static CallNotesResolution Success(IReadOnlyList<string> notes) => new(notes, null);

        public static CallNotesResolution Error(string errorMessage) => new([], errorMessage);
    }

    private sealed record CallQuestionsResolution(IReadOnlyList<CallQuestionInput> Questions, string? ErrorMessage)
    {
        public static CallQuestionsResolution Success(IReadOnlyList<CallQuestionInput> questions) => new(questions, null);

        public static CallQuestionsResolution Error(string errorMessage) => new([], errorMessage);
    }

    private sealed record CallWaitConfiguration(TimeSpan PollEvery, TimeSpan? Timeout);

    private sealed record CallWaitConfigurationResolution(CallWaitConfiguration? Value, string? ErrorMessage)
    {
        public static CallWaitConfigurationResolution Success(CallWaitConfiguration value) => new(value, null);

        public static CallWaitConfigurationResolution Error(string errorMessage) => new(null, errorMessage);
    }

    private sealed record InteractiveSuggestionsResolution(IReadOnlyList<InteractiveSuggestion> Suggestions, ApiCallResult? ErrorResult)
    {
        public static InteractiveSuggestionsResolution Success(IReadOnlyList<InteractiveSuggestion> suggestions) => new(suggestions, null);

        public static InteractiveSuggestionsResolution ApiError(ApiCallResult result) => new([], result);
    }

    private sealed record ResolvedOrganisation(string Reference, int Id, string Name, bool ActiveLicenseHotline);

    private sealed record OrganisationReferenceResolution(ResolvedOrganisation? Organisation, string? ErrorMessage, ApiCallResult? ErrorResult)
    {
        public static OrganisationReferenceResolution Success(ResolvedOrganisation organisation) => new(organisation, null, null);

        public static OrganisationReferenceResolution Error(string errorMessage) => new(null, errorMessage, null);

        public static OrganisationReferenceResolution ApiError(ApiCallResult errorResult) => new(null, null, errorResult);
    }

    private sealed record ValidatedTemplateReference(string Uuid, int? Id, string? Name);

    private sealed record TemplateVersionInfo(int Id, string Description);

    private sealed record TemplateReferenceResolution(
        ValidatedTemplateReference? Template,
        IReadOnlyList<TemplateVersionInfo> AvailableVersions,
        string? ErrorMessage,
        ApiCallResult? ErrorResult)
    {
        public static TemplateReferenceResolution Success(ValidatedTemplateReference template, IReadOnlyList<TemplateVersionInfo> availableVersions) =>
            new(template, availableVersions, null, null);

        public static TemplateReferenceResolution Error(string errorMessage) => new(null, [], errorMessage, null);

        public static TemplateReferenceResolution ApiError(ApiCallResult errorResult) => new(null, [], null, errorResult);
    }

    private sealed record CallStatusResolution(
        JsonObject? JsonPayload,
        JsonObject? HumanPayload,
        bool TimedOut,
        bool HasResult,
        string? ErrorMessage,
        ApiCallResult? ErrorResult)
    {
        public static CallStatusResolution Success(JsonObject jsonPayload, JsonObject humanPayload, bool timedOut, bool hasResult) =>
            new(jsonPayload, humanPayload, timedOut, hasResult, null, null);

        public static CallStatusResolution Error(string errorMessage) =>
            new(null, null, false, false, errorMessage, null);

        public static CallStatusResolution ApiError(ApiCallResult errorResult) =>
            new(null, null, false, false, null, errorResult);
    }

    private sealed record DeleteCallResultResolution(
        string? DeletedCallResultUuid,
        string? WarningMessage,
        string? ErrorMessage,
        ApiCallResult? ErrorResult)
    {
        public static DeleteCallResultResolution Success(string deletedCallResultUuid) => new(deletedCallResultUuid, null, null, null);

        public static DeleteCallResultResolution Warning(string warningMessage) => new(null, warningMessage, null, null);

        public static DeleteCallResultResolution Error(string errorMessage) => new(null, null, errorMessage, null);

        public static DeleteCallResultResolution ApiError(ApiCallResult errorResult) => new(null, null, null, errorResult);
    }

    private static class CallPromptSentinel
    {
        public const int CancelledInt = int.MinValue;
    }

    private sealed record ApiClientResolution(IHalleyApiClient? Value, string? ErrorMessage)
    {
        public bool IsError => !string.IsNullOrWhiteSpace(ErrorMessage);

        public static ApiClientResolution Success(IHalleyApiClient value) => new(value, null);

        public static ApiClientResolution Error(string errorMessage) => new(null, errorMessage);
    }
}
