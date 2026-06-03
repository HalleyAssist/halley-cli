using System.CommandLine;
using Halley.App.Api;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Halley.App.Main;

public sealed class HalleyCliApplication
{
    private static readonly Logger SilentLogger = new LoggerConfiguration().CreateLogger();

    private readonly Func<HalleyApiClientOptions, ILogger, IHalleyApiClient> _apiClientFactory;
    private readonly ISessionStore _sessionStore;
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly IPasswordPrompt _passwordPrompt;
    private readonly IHalleyLoggerFactory _loggerFactory;
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
        IPasswordPrompt? passwordPrompt = null,
        IHalleyLoggerFactory? loggerFactory = null)
    {
        _apiClientFactory = apiClientFactory;
        _sessionStore = sessionStore;
        _stdout = stdout ?? Console.Out;
        _stderr = stderr ?? Console.Error;
        _passwordPrompt = passwordPrompt ?? new ConsolePasswordPrompt();
        _loggerFactory = loggerFactory ?? new SerilogLoggerFactory();
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
                password = await _passwordPrompt.ReadPasswordAsync(_stderr, cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                return await WriteCliErrorAsync("A password is required.", outputMode, cancellationToken);
            }

            var request = new UserLoginRequest(
                parseResult.GetRequiredValue(usernameOption),
                password);

            var result = await apiClient.Value!.LoginUserAsync(request, cancellationToken);
            return await HandleLoginResultAsync(result, "user", outputMode, cancellationToken);
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
            return await HandleLoginResultAsync(result, "api-key", outputMode, cancellationToken);
        });

        return command;
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
        command.Add(CreateOrganisationsPatchCommand());
        command.Add(CreateOrganisationsPutCommand());
        command.Add(CreateOrganisationsDeleteCommand());
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

    private Command CreateOrganisationsPatchCommand()
    {
        var idArgument = new Argument<int>("organisation-id") { Description = "The organisation id." };
        var options = CreateOrganisationWriteOptions();

        var command = new Command("patch", "Patch an existing organisation.");
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

    private Command CreateOrganisationsPutCommand()
    {
        var idArgument = new Argument<int>("organisation-id") { Description = "The organisation id." };
        var options = CreateOrganisationWriteOptions();

        var command = new Command("put", "Replace an existing organisation.");
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
            var result = await apiClient.Value!.PutOrganisationAsync(token, parseResult.GetRequiredValue(idArgument), request, cancellationToken);
            return await HandleApiResultAsync(result, outputMode, cancellationToken);
        });

        return command;
    }

    private Command CreateOrganisationsDeleteCommand()
    {
        var idArgument = new Argument<int>("organisation-id") { Description = "The organisation id." };

        var command = new Command("delete", "Delete an organisation.");
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

            var result = await apiClient.Value!.DeleteOrganisationAsync(token, parseResult.GetRequiredValue(idArgument), cancellationToken);
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
        command.Add(CreateUsersPatchCommand());
        command.Add(CreateUsersPutCommand());
        command.Add(CreateUsersDeleteCommand());
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

    private Command CreateUsersPatchCommand()
    {
        var targetNameArgument = new Argument<string>("name") { Description = "The user name to update." };
        var options = CreateUserWriteOptions(requireCountry: false, useNewNameOption: true);

        var command = new Command("patch", "Patch an existing user.");
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

    private Command CreateUsersPutCommand()
    {
        var targetNameArgument = new Argument<string>("name") { Description = "The user name to replace." };
        var options = CreateUserWriteOptions(requireCountry: false, useNewNameOption: true);

        var command = new Command("put", "Replace an existing user.");
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

            var result = await apiClient.Value!.PutUserAsync(token, targetName, request!, cancellationToken);
            return await HandleApiResultAsync(result, outputMode, cancellationToken);
        });

        return command;
    }

    private Command CreateUsersDeleteCommand()
    {
        var nameArgument = new Argument<string>("name") { Description = "The user name." };

        var command = new Command("delete", "Delete a user.");
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

            var result = await apiClient.Value!.DeleteUserAsync(token, parseResult.GetRequiredValue(nameArgument), cancellationToken);
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
        var contactPhoneOption = CreateOption<string?>("--contact-phone", "Optional contact phone number.");

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

    private async Task<int> HandleLoginResultAsync(ApiCallResult result, string authType, OutputMode outputMode, CancellationToken cancellationToken)
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
            await _sessionStore.SaveAsync(new SessionRecord(token, authType, DateTimeOffset.UtcNow), cancellationToken);
            CurrentLogger.Information("Saved {AuthType} session token to {SessionPath}.", authType, _sessionStore.SessionPath);
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
            return explicitToken;
        }

        try
        {
            var session = await _sessionStore.LoadAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(session?.Token))
            {
                CurrentLogger.Information("Loaded a saved session token from {SessionPath}.", _sessionStore.SessionPath);
                return session.Token;
            }

            CurrentLogger.Warning("No saved session token was found at {SessionPath}.", _sessionStore.SessionPath);
            await WriteCliErrorAsync("No saved session token was found. Run `login ...` first or pass `--token`.", outputMode, cancellationToken);
            return null;
        }
        catch (Exception ex)
        {
            CurrentLogger.Error(ex, "Failed to read session from {SessionPath}.", _sessionStore.SessionPath);
            await WriteCliErrorAsync($"Failed to read session from {_sessionStore.SessionPath}: {ex.Message}", outputMode, cancellationToken);
            return null;
        }
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

    private sealed record ApiClientResolution(IHalleyApiClient? Value, string? ErrorMessage)
    {
        public bool IsError => !string.IsNullOrWhiteSpace(ErrorMessage);

        public static ApiClientResolution Success(IHalleyApiClient value) => new(value, null);

        public static ApiClientResolution Error(string errorMessage) => new(null, errorMessage);
    }
}
