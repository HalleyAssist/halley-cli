using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Halley.App.Api;
using Halley.App.Main;

namespace Halley.App.Tests;

public sealed class HalleyCliApplicationTests
{
    private static readonly Lock HeadlessLock = new();
    private static Thread? _headlessThread;
    private static AutoResetEvent? _headlessInitializedEvent;
    private static AutoResetEvent? _headlessWorkAvailableEvent;
    private static AutoResetEvent? _headlessWorkCompletedEvent;
    private static Action? _pendingHeadlessAction;
    private static Exception? _pendingHeadlessException;
    private static bool _headlessInitialized;

    [Fact]
    public async Task LoginUserSavesSessionAndPrintsHumanToken()
    {
        using var harness = new TestHarness((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://cloud.halleyassist.com/login", request.RequestUri?.ToString());

            var payload = JsonNode.Parse(body!)!.AsObject();
            Assert.Equal("alice", payload["username"]?.GetValue<string>());
            Assert.Equal("secret", payload["password"]?.GetValue<string>());

            return JsonResponse(HttpStatusCode.Created, """{"token":"user-token"}""");
        });

        var exitCode = await harness.RunAsync("login", "user", "--username", "alice", "--password", "secret");

        Assert.Equal(0, exitCode);
        Assert.Equal("user-token", harness.StdoutText.Trim());
        Assert.Equal(string.Empty, harness.StderrText);

        var session = await harness.SessionStore.LoadAsync(DefaultSessionKey);
        Assert.NotNull(session);
        Assert.Equal("user-token", session!.Token);
        Assert.Equal("user", session.AuthType);
    }

    [Fact]
    public async Task LoginUserStoresSavedAtUsingTheLocalTimestampFormat()
    {
        var clock = new FakeAsyncClock(new DateTimeOffset(2026, 2, 18, 18, 55, 58, 611, TimeSpan.FromHours(10)));
        using var harness = new TestHarness(
            (_, _) => JsonResponse(HttpStatusCode.Created, """{"token":"user-token"}"""),
            clock: clock);

        var exitCode = await harness.RunAsync("login", "user", "--username", "alice", "--password", "secret");

        Assert.Equal(0, exitCode);

        var json = await File.ReadAllTextAsync(harness.SessionStore.SessionPath);
        Assert.Contains("\"savedAt\": \"2026-02-18T18:55:58.611+10:00\"", json);
        Assert.DoesNotContain("savedAtUtc", json);
    }

    [Fact]
    public async Task LoginUserPromptsForPasswordWhenOptionIsMissing()
    {
        var interactiveUi = new StubInteractiveUi(isInteractive: true, password: "prompt-secret");
        using var harness = new TestHarness((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://cloud.halleyassist.com/login", request.RequestUri?.ToString());

            var payload = JsonNode.Parse(body!)!.AsObject();
            Assert.Equal("alice", payload["username"]?.GetValue<string>());
            Assert.Equal("prompt-secret", payload["password"]?.GetValue<string>());

            return JsonResponse(HttpStatusCode.Created, """{"token":"user-token"}""");
        }, interactiveUi: interactiveUi);

        var exitCode = await harness.RunAsync("login", "user", "--username", "alice");

        Assert.Equal(0, exitCode);
        Assert.Equal(1, interactiveUi.PasswordCallCount);
        Assert.Equal("user-token", harness.StdoutText.Trim());
    }

    [Fact]
    public async Task LoginUserReturnsCliErrorWhenPromptedPasswordIsEmpty()
    {
        var interactiveUi = new StubInteractiveUi(isInteractive: true, password: string.Empty);
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.Created, """{"token":"unused"}"""), interactiveUi: interactiveUi);

        var exitCode = await harness.RunAsync("login", "user", "--username", "alice");

        Assert.Equal(1, exitCode);
        Assert.Equal(1, interactiveUi.PasswordCallCount);
        Assert.Contains("A password is required.", harness.StderrText);
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public async Task LoginApiKeySavesSessionAndPrintsJsonToken()
    {
        using var harness = new TestHarness((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://cloud.halleyassist.com/auth/api_key", request.RequestUri?.ToString());

            var payload = JsonNode.Parse(body!)!.AsObject();
            Assert.Equal("very-secret", payload["secret"]?.GetValue<string>());

            return JsonResponse(HttpStatusCode.Created, """{"token":"api-token"}""");
        });

        var exitCode = await harness.RunAsync("login", "api-key", "--secret", "very-secret", "--output", "json");

        Assert.Equal(0, exitCode);

        var output = JsonNode.Parse(harness.StdoutText)!.AsObject();
        Assert.Equal("api-token", output["token"]?.GetValue<string>());

        var session = await harness.SessionStore.LoadAsync(DefaultSessionKey);
        Assert.NotNull(session);
        Assert.Equal("api-token", session!.Token);
        Assert.Equal("api-key", session.AuthType);
    }

    [Fact]
    public async Task UsersMeUsesSavedSessionTokenAndDefaultsToHumanOutput()
    {
        using var harness = new TestHarness((request, _) =>
        {
            Assert.Equal("Bearer stored-token", request.Headers.Authorization?.ToString());
            return JsonResponse(HttpStatusCode.OK, """{"user":{"id":1,"name":"alice","email":"alice@example.test","role":"user","avatar_url":"https://example.test/avatar.png"}}""");
        });

        await harness.SessionStore.SaveAsync(DefaultSessionKey, new SessionRecord("stored-token", "user", DateTimeOffset.UtcNow));

        var exitCode = await harness.RunAsync("users", "me");

        Assert.Equal(0, exitCode);
        Assert.Contains("Field", harness.StdoutText);
        Assert.Contains("alice", harness.StdoutText);
        Assert.DoesNotContain("{", harness.StdoutText);
    }

    [Fact]
    public async Task SessionsAreLoadedPerEndpoint()
    {
        using var harness = new TestHarness((request, _) =>
        {
            Assert.Equal("Bearer custom-token", request.Headers.Authorization?.ToString());
            Assert.Equal("https://cloud.blah.halleyassist.com/api/v1/users/_me", request.RequestUri?.ToString());
            return JsonResponse(HttpStatusCode.OK, """{"user":{"name":"alice"}}""");
        });

        await harness.SessionStore.SaveAsync(DefaultSessionKey, new SessionRecord("default-token", "user", DateTimeOffset.UtcNow));
        await harness.SessionStore.SaveAsync("https://cloud.blah.halleyassist.com", new SessionRecord("custom-token", "user", DateTimeOffset.UtcNow));

        var exitCode = await harness.RunAsync("users", "me", "--endpoint", "https://cloud.blah.halleyassist.com");

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task UsersMeFailsWhenSavedSessionJwtIsExpired()
    {
        var clock = new FakeAsyncClock();
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.OK, """{"user":{"name":"alice"}}"""), clock: clock);

        await harness.SessionStore.SaveAsync(
            DefaultSessionKey,
            new SessionRecord(CreateJwt(clock.UtcNow.AddMinutes(-5)), "user", clock.UtcNow));

        var exitCode = await harness.RunAsync("users", "me");

        Assert.Equal(1, exitCode);
        Assert.Contains("saved session token", harness.StderrText);
        Assert.Contains("Run `login ...` again", harness.StderrText);
        Assert.Contains("2026-02-18 08:50:58Z", harness.StderrText);
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public async Task UsersMeFailsWhenExplicitJwtIsExpired()
    {
        var clock = new FakeAsyncClock();
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.OK, """{"user":{"name":"alice"}}"""), clock: clock);

        var exitCode = await harness.RunAsync("users", "me", "--token", CreateJwt(clock.UtcNow.AddMinutes(-1)));

        Assert.Equal(1, exitCode);
        Assert.Contains("supplied JWT has expired", harness.StderrText);
        Assert.Contains("Run `login ...` again", harness.StderrText);
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public async Task LoginTokensListsAllKnownSessions()
    {
        var clock = new FakeAsyncClock();
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.OK, """{}"""), clock: clock);

        await harness.SessionStore.SaveAsync(DefaultSessionKey, new SessionRecord(CreateJwt(clock.UtcNow.AddHours(1)), "user", clock.UtcNow));
        await harness.SessionStore.SaveAsync("https://cloud.blah.halleyassist.com", new SessionRecord(CreateJwt(clock.UtcNow.AddMinutes(-5)), "api-key", clock.UtcNow.AddMinutes(-10)));

        var exitCode = await harness.RunAsync("login", "tokens");

        Assert.Equal(0, exitCode);
        Assert.Contains("endpoint", harness.StdoutText);
        Assert.Contains(DefaultSessionKey, harness.StdoutText);
        Assert.Contains("https://cloud.blah.halleyassist.com", harness.StdoutText);
        Assert.Contains("api-key", harness.StdoutText);
        Assert.Contains("true", harness.StdoutText);
        Assert.Contains("false", harness.StdoutText);
        Assert.Contains("signature", harness.StdoutText);
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public async Task LoginTokensRendersJsonWhenRequested()
    {
        var clock = new FakeAsyncClock();
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.OK, """{}"""), clock: clock);

        await harness.SessionStore.SaveAsync(DefaultSessionKey, new SessionRecord(CreateJwt(clock.UtcNow.AddHours(1)), "user", clock.UtcNow));

        var exitCode = await harness.RunAsync("login", "tokens", "--output", "json");

        Assert.Equal(0, exitCode);
        var payload = JsonNode.Parse(harness.StdoutText)!.AsObject();
        Assert.Equal(DefaultSessionKey, payload["tokens"]?[0]?["endpoint"]?.GetValue<string>());
        Assert.Equal("user", payload["tokens"]?[0]?["auth_type"]?.GetValue<string>());
        Assert.Equal(false, payload["tokens"]?[0]?["expired"]?.GetValue<bool>());
        Assert.NotNull(payload["tokens"]?[0]?["token"]?.GetValue<string>());
    }

    [Fact]
    public async Task LoginEditCreatesTheSessionFileAndOpensItInTheEditor()
    {
        var textFileEditor = new StubTextFileEditor();
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.OK, """{}"""), textFileEditor: textFileEditor);

        var exitCode = await harness.RunAsync("login", "edit");

        Assert.Equal(0, exitCode);
        Assert.Single(textFileEditor.OpenedPaths);
        Assert.Equal(harness.SessionStore.SessionPath, textFileEditor.OpenedPaths[0]);
        Assert.True(File.Exists(harness.SessionStore.SessionPath));
        Assert.Contains("Opened local auth file.", harness.StdoutText);
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public async Task UsersListRendersHumanTableByDefault()
    {
        using var harness = new TestHarness((request, _) =>
        {
            Assert.Equal("https://cloud.halleyassist.com/api/v1/users", request.RequestUri?.ToString());
            return JsonResponse(HttpStatusCode.OK, """{"users":[{"id":1,"name":"alice"},{"id":2,"name":"bob"}]}""");
        });

        var exitCode = await harness.RunAsync("users", "list", "--token", "inline-token");

        Assert.Equal(0, exitCode);
        Assert.Contains("id", harness.StdoutText);
        Assert.Contains("alice", harness.StdoutText);
        Assert.DoesNotContain("\"users\"", harness.StdoutText);
    }

    [Fact]
    public async Task UsersListRendersJsonWhenRequested()
    {
        using var harness = new TestHarness((_, _) =>
            JsonResponse(HttpStatusCode.OK, """{"users":[{"id":1,"name":"alice"}]}"""));

        var exitCode = await harness.RunAsync("users", "list", "--token", "inline-token", "--output", "json");

        Assert.Equal(0, exitCode);
        var payload = JsonNode.Parse(harness.StdoutText)!.AsObject();
        Assert.Equal("alice", payload["users"]?[0]?["name"]?.GetValue<string>());
    }

    [Fact]
    public async Task LogDefaultsToWarningAndWritesNothingForSuccessfulRequests()
    {
        using var harness = new TestHarness((_, _) =>
            JsonResponse(HttpStatusCode.OK, """{"users":[{"id":1,"name":"alice"}]}"""));

        var exitCode = await harness.RunAsync("users", "list", "--token", "inline-token", "--output", "json");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, harness.StderrText);
    }

    [Fact]
    public async Task LogOptionWithoutLevelUsesInfoAndWritesRequestAndResponseToStderr()
    {
        using var harness = new TestHarness((_, _) =>
            JsonResponse(HttpStatusCode.OK, """{"users":[{"id":1,"name":"alice"}]}"""));

        var exitCode = await harness.RunAsync("users", "list", "--token", "inline-token", "--output", "json", "--log");

        Assert.Equal(0, exitCode);

        var payload = JsonNode.Parse(harness.StdoutText)!.AsObject();
        Assert.Equal("alice", payload["users"]?[0]?["name"]?.GetValue<string>());
        Assert.Contains("HTTP GET", harness.StderrText);
        Assert.Contains("https://cloud.halleyassist.com/api/v1/users", harness.StderrText);
        Assert.Contains("responded 200 OK", harness.StderrText);
        Assert.Contains("Request body: <empty>", harness.StderrText);
        Assert.Contains("< HTTP/1.1 200 OK", harness.StderrText);
        Assert.Contains("< Content-Type: application/json; charset=utf-8", harness.StderrText);
        Assert.DoesNotContain("Response body:", harness.StderrText);
        Assert.Equal(1, CountOccurrences(harness.StderrText, "alice"));
    }

    [Fact]
    public async Task DebugLogIncludesCurlStyleRequestResponseAndHeaders()
    {
        using var harness = new TestHarness((_, _) =>
        {
            var response = JsonResponse(HttpStatusCode.Created, """{"api_key":{"id":"123"}}""");
            response.Headers.Add("X-Trace-Id", "trace-123");
            return response;
        });

        var exitCode = await harness.RunAsync(
            "api-keys",
            "create",
            "--organisation-id", "7",
            "--permission", "ops_read_hubs",
            "--token", "inline-token",
            "--output", "json",
            "--log", "debug");

        Assert.Equal(0, exitCode);
        Assert.Contains("> POST /api/v1/api_keys HTTP/1.1", harness.StderrText);
        Assert.Contains("> Host: cloud.halleyassist.com", harness.StderrText);
        Assert.Contains("> Authorization: [redacted]", harness.StderrText);
        Assert.Contains("> Content-Type: application/json; charset=utf-8", harness.StderrText);
        Assert.Contains("\"organisation_id\":7", harness.StderrText);
        Assert.Contains("< HTTP/1.1 201 Created", harness.StderrText);
        Assert.Contains("< X-Trace-Id: trace-123", harness.StderrText);
        Assert.Contains("< Content-Type: application/json; charset=utf-8", harness.StderrText);
        Assert.Contains("\"api_key\":{\"id\":\"123\"}", harness.StderrText);
        Assert.Equal(1, CountOccurrences(harness.StderrText, "\"api_key\":{\"id\":\"123\"}"));
        Assert.DoesNotContain("inline-token", harness.StderrText);
    }

    [Fact]
    public async Task LogOutputRedactsSensitiveValues()
    {
        using var harness = new TestHarness((_, _) =>
            JsonResponse(HttpStatusCode.Created, """{"token":"user-token"}"""));

        var exitCode = await harness.RunAsync("login", "user", "--username", "alice", "--password", "secret", "--log", "info");

        Assert.Equal(0, exitCode);
        Assert.Contains("HTTP POST", harness.StderrText);
        Assert.Contains("https://cloud.halleyassist.com/login", harness.StderrText);
        Assert.Contains("responded 201 Created", harness.StderrText);
        Assert.Contains("\"password\":\"[redacted]\"", harness.StderrText);
        Assert.DoesNotContain("secret", harness.StderrText);
        Assert.Contains("\"token\":\"[redacted]\"", harness.StderrText);
    }

    [Fact]
    public async Task InvalidLogLevelReturnsCliError()
    {
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.OK, """{}"""));

        var exitCode = await harness.RunAsync("users", "me", "--token", "inline-token", "--log", "loud");

        Assert.Equal(1, exitCode);
        Assert.Contains("Invalid --log level `loud`", harness.StderrText);
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public async Task AuthenticationErrorsTellTheUserToLoginAgain()
    {
        using var harness = new TestHarness((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("<html>login</html>")
        });

        var exitCode = await harness.RunAsync("users", "me", "--token", "inline-token");

        Assert.Equal(1, exitCode);
        Assert.Contains("Authentication failed.", harness.StderrText);
        Assert.Contains("Run `login ...` again", harness.StderrText);
        Assert.Contains("401 Unauthorized", harness.StderrText);
    }

    [Fact]
    public async Task ApiKeysCreateMapsPermissionsAndExpiry()
    {
        using var harness = new TestHarness((request, body) =>
        {
            Assert.Equal("Bearer inline-token", request.Headers.Authorization?.ToString());
            Assert.Equal("https://cloud.halleyassist.com/api/v1/api_keys", request.RequestUri?.ToString());

            var payload = JsonNode.Parse(body!)!.AsObject()["api_key"]!.AsObject();
            Assert.Equal(7, payload["organisation_id"]?.GetValue<int>());
            Assert.Equal("ops_read_hubs", payload["permissions"]?[0]?.GetValue<string>());
            Assert.Equal("ops_read_organisations", payload["permissions"]?[1]?.GetValue<string>());
            Assert.Equal("2026-06-01T00:00:00+00:00", payload["expires_at"]?.GetValue<string>());

            return JsonResponse(HttpStatusCode.Created, """{"api_key":{"id":"123"}}""");
        });

        var exitCode = await harness.RunAsync(
            "api-keys",
            "create",
            "--organisation-id", "7",
            "--permission", "ops_read_hubs",
            "--permission", "ops_read_organisations",
            "--expires-at", "2026-06-01T00:00:00Z",
            "--token", "inline-token",
            "--output", "json");

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task UsersCreateBuildsNestedContactPayload()
    {
        using var harness = new TestHarness((request, body) =>
        {
            Assert.Equal("https://cloud.halleyassist.com/api/v1/users", request.RequestUri?.ToString());

            var user = JsonNode.Parse(body!)!.AsObject()["user"]!.AsObject();
            Assert.Equal("alice", user["name"]?.GetValue<string>());
            Assert.Equal("pw123", user["password"]?.GetValue<string>());
            Assert.Equal("AU", user["country"]?.GetValue<string>());

            var contact = user["contact"]!.AsObject();
            Assert.Equal("Alice Example", contact["name"]?.GetValue<string>());
            Assert.Equal("user", contact["role"]?.GetValue<string>());
            Assert.Equal("email", contact["details"]?[0]?["type"]?.GetValue<string>());
            Assert.Equal("alice@example.test", contact["details"]?[0]?["value"]?.GetValue<string>());
            Assert.Equal("phone", contact["details"]?[1]?["type"]?.GetValue<string>());
            Assert.Equal("0400000000", contact["details"]?[1]?["value"]?.GetValue<string>());

            return JsonResponse(HttpStatusCode.Created, """{"user":{"id":1,"name":"alice"}}""");
        });

        var exitCode = await harness.RunAsync(
            "users",
            "create",
            "--name", "alice",
            "--password", "pw123",
            "--country", "AU",
            "--contact-name", "Alice Example",
            "--contact-email", "alice@example.test",
            "--contact-phone", "0400000000",
            "--token", "inline-token");

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task UsersCreateRejectsInvalidContactCombination()
    {
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.OK, """{}"""));

        var exitCode = await harness.RunAsync(
            "users",
            "create",
            "--name", "alice",
            "--password", "pw123",
            "--country", "AU",
            "--contact-id", "42",
            "--contact-name", "Alice Example",
            "--contact-email", "alice@example.test",
            "--token", "inline-token");

        Assert.Equal(1, exitCode);
        Assert.Contains("--contact-id cannot be used together", harness.StderrText);
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public async Task UsersCreateRejectsInvalidContactPhone()
    {
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.OK, """{}"""));

        var exitCode = await harness.RunAsync(
            "users",
            "create",
            "--name", "alice",
            "--password", "pw123",
            "--country", "AU",
            "--contact-name", "Alice Example",
            "--contact-email", "alice@example.test",
            "--contact-phone", "not-a-phone",
            "--token", "inline-token");

        Assert.Equal(1, exitCode);
        Assert.Contains("Invalid --contact-phone value. Expected a valid phone number. Use an international number such as `+61400000000`, or a national-format number that matches the supplied country.", harness.StderrText);
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public async Task UsersUpdateDefaultsNameToTargetWhenNewNameIsOmitted()
    {
        using var harness = new TestHarness((request, body) =>
        {
            Assert.Equal("https://cloud.halleyassist.com/api/v1/users/alice", request.RequestUri?.ToString());
            var user = JsonNode.Parse(body!)!.AsObject()["user"]!.AsObject();
            Assert.Equal("alice", user["name"]?.GetValue<string>());
            Assert.Equal("pw123", user["password"]?.GetValue<string>());
            return JsonResponse(HttpStatusCode.OK, """{"user":{"id":1,"name":"alice"}}""");
        });

        var exitCode = await harness.RunAsync(
            "users",
            "update",
            "alice",
            "--password", "pw123",
            "--token", "inline-token");

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task UsersUpdateRejectsInvalidContactPhone()
    {
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.OK, """{}"""));

        var exitCode = await harness.RunAsync(
            "users",
            "update",
            "alice",
            "--password", "pw123",
            "--country", "AU",
            "--contact-name", "Alice Example",
            "--contact-email", "alice@example.test",
            "--contact-phone", "not-a-phone",
            "--token", "inline-token");

        Assert.Equal(1, exitCode);
        Assert.Contains("Invalid --contact-phone value. Expected a valid phone number. Use an international number such as `+61400000000`, or a national-format number that matches the supplied country.", harness.StderrText);
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public async Task UsersUpdateRejectsNationalContactPhoneWithoutCountry()
    {
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.OK, """{}"""));

        var exitCode = await harness.RunAsync(
            "users",
            "update",
            "alice",
            "--password", "pw123",
            "--contact-name", "Alice Example",
            "--contact-email", "alice@example.test",
            "--contact-phone", "0400000000",
            "--token", "inline-token");

        Assert.Equal(1, exitCode);
        Assert.Contains("Invalid --contact-phone value. Use an international phone number such as `+61400000000`, or supply `--country` when using a national-format number.", harness.StderrText);
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public async Task UsersHelpShowsUpdateAndOmitsPutAndDelete()
    {
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.OK, """{}"""));

        var exitCode = await harness.RunAsync("users", "--help");

        Assert.Equal(0, exitCode);
        Assert.Contains("Update an existing user.", harness.StdoutText);
        Assert.DoesNotContain("Patch an existing user.", harness.StdoutText);
        Assert.DoesNotContain("Replace an existing user.", harness.StdoutText);
        Assert.DoesNotContain("Delete a user.", harness.StdoutText);
    }

    [Fact]
    public async Task OrganisationsHelpShowsUpdateAndOmitsPutAndDelete()
    {
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.OK, """{}"""));

        var exitCode = await harness.RunAsync("organisations", "--help");

        Assert.Equal(0, exitCode);
        Assert.Contains("Update an existing organisation.", harness.StdoutText);
        Assert.DoesNotContain("Patch an existing organisation.", harness.StdoutText);
        Assert.DoesNotContain("Replace an existing organisation.", harness.StdoutText);
        Assert.DoesNotContain("Delete an organisation.", harness.StdoutText);
    }

    [Fact]
    public async Task ApiErrorsReturnNonZeroAndJsonPayloadInJsonMode()
    {
        using var harness = new TestHarness((_, _) =>
            JsonResponse(HttpStatusCode.NotFound, """{"error":"missing"}"""));

        var exitCode = await harness.RunAsync("users", "get", "missing", "--token", "inline-token", "--output", "json", "--log", "none");

        Assert.Equal(1, exitCode);
        var payload = JsonNode.Parse(harness.StderrText)!.AsObject();
        Assert.Equal("missing", payload["error"]?.GetValue<string>());
    }

    [Theory]
    [InlineData("halleyassist.com", "https://cloud.halleyassist.com/auth/api_key")]
    [InlineData("halleyassist.com/api", "https://cloud.halleyassist.com/auth/api_key")]
    [InlineData("cloud.halleyassist.com", "https://cloud.halleyassist.com/auth/api_key")]
    [InlineData("https://cloud.blah.halleyassist.com", "https://cloud.blah.halleyassist.com/auth/api_key")]
    [InlineData("https://cloud.blah.halleyassist.com/api", "https://cloud.blah.halleyassist.com/auth/api_key")]
    public async Task EndpointAcceptsHumanFriendlyInputForAuthRequests(string endpointInput, string expectedAuthUrl)
    {
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.Created, """{"token":"endpoint-token"}"""));

        var exitCode = await harness.RunAsync(
            "login",
            "api-key",
            "--secret", "very-secret",
            "--endpoint", endpointInput);

        Assert.Equal(0, exitCode);
        Assert.Single(harness.Requests);
        Assert.Equal(expectedAuthUrl, harness.Requests[0].Uri);
    }

    [Fact]
    public async Task EndpointFullUrlWithApiSuffixBuildsApiRequestsCorrectly()
    {
        using var harness = new TestHarness((_, _) =>
            JsonResponse(HttpStatusCode.OK, """{"users":[{"id":1,"name":"alice"}]}"""));

        var exitCode = await harness.RunAsync(
            "users",
            "list",
            "--token", "inline-token",
            "--endpoint", "https://cloud.blah.halleyassist.com/api");

        Assert.Equal(0, exitCode);
        Assert.Single(harness.Requests);
        Assert.Equal("https://cloud.blah.halleyassist.com/api/v1/users", harness.Requests[0].Uri);
    }

    [Fact]
    public async Task InvalidEndpointReturnsCliError()
    {
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.OK, """{}"""));

        var exitCode = await harness.RunAsync(
            "login",
            "user",
            "--username", "alice",
            "--password", "secret",
            "--endpoint", "ftp://halleyassist.com");

        Assert.Equal(1, exitCode);
        Assert.Contains("Endpoint must use http or https", harness.StderrText);
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public async Task CallsCreateBuildsTemplateAndManualPayload()
    {
        using var harness = new TestHarness((request, body) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/organisations/50" => OrganisationResponse(),
                "/api/v1/call_templates/7" => CallTemplateResponse(7, "template-uuid", "Wellbeing Check", 3),
                "/api/v1/call_requests" => AssertTemplateAndManualCallRequest(body!),
                _ => throw new InvalidOperationException(request.RequestUri!.ToString())
            };
        });

        var exitCode = await harness.RunAsync(
            "calls",
            "create",
            "--organisation-id", "50",
            "--call-method", "phone",
            "--phone-number", "+61400000000",
            "--recipient-name", "Test User",
            "--recipient-timezone", "Australia/Melbourne",
            "--template-uuid", "template-uuid",
            "--template-id", "7",
            "--instructions", "Please check in",
            "--agenda", "Say hello",
            "--note", "Weather was warm",
            "--question", "1:boolean:Was the resident okay?",
            "--token", "inline-token",
            "--output", "json");

        Assert.Equal(0, exitCode);
        Assert.Equal("request-1", JsonNode.Parse(harness.StdoutText)!["call_request"]?["uuid"]?.GetValue<string>());
        Assert.DoesNotContain("To make this call again execute:", harness.StderrText);
    }

    [Fact]
    public async Task CallsCreateUsesActiveTemplateVersionWhenTemplateIdIsOmitted()
    {
        using var harness = new TestHarness((request, body) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/organisations/50" => OrganisationResponse(),
                "/api/v1/call_templates/template-uuid" => CallTemplateResponseWithVersions(
                    7,
                    (9, "template-uuid", "Wellbeing Check", 4),
                    (7, "template-uuid", "Wellbeing Check", 3)),
                "/api/v1/call_requests" => AssertActiveTemplateVersionCallRequest(body!),
                _ => throw new InvalidOperationException(request.RequestUri!.ToString())
            };
        });

        var exitCode = await harness.RunAsync(
            "calls",
            "create",
            "--organisation-id", "50",
            "--call-method", "web",
            "--recipient-name", "Test User",
            "--recipient-timezone", "Australia/Melbourne",
            "--template-uuid", "template-uuid",
            "--token", "inline-token",
            "--output", "json");

        Assert.Equal(0, exitCode);
        Assert.Equal("request-active-template", JsonNode.Parse(harness.StdoutText)!["call_request"]?["uuid"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallsCreateReadsInstructionsAndAgendaFromFiles()
    {
        using var harness = new TestHarness((request, body) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/organisations/50" => OrganisationResponse(),
                "/api/v1/call_requests" => AssertFileBackedCallRequest(body!),
                _ => throw new InvalidOperationException(request.RequestUri!.ToString())
            };
        });

        var instructionsPath = Path.Combine(harness.TempDirectory, "instructions.txt");
        var agendaPath = Path.Combine(harness.TempDirectory, "agenda.txt");
        await File.WriteAllTextAsync(instructionsPath, "Line one\nLine two");
        await File.WriteAllTextAsync(agendaPath, "Agenda one");

        var exitCode = await harness.RunAsync(
            "calls",
            "create",
            "--organisation-id", "50",
            "--call-method", "web",
            "--recipient-name", "Test User",
            "--recipient-timezone", "Australia/Melbourne",
            "--instructions-file", instructionsPath,
            "--agenda-file", agendaPath,
            "--token", "inline-token");

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task CallsCreatePromptsInteractivelyWhenNoCreateOptionsAreProvided()
    {
        var clock = new FakeAsyncClock();
        var interactivePrompter = new StubInteractiveUi(
            isInteractive: true,
            lineResponses:
            [
                "manual",
                "Acme Care",
                "phone",
                "+61400000000",
                "Test User",
                "Australia/Melbourne",
                "n",
                "n"
            ],
            multilineResponses:
            [
                "Please check in",
                string.Empty
            ]);

        using var harness = new TestHarness((request, body) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/organisations" when request.RequestUri!.Query.Contains("size=200", StringComparison.Ordinal) => OrganisationsListResponse((50, "Acme Care", true), (51, "No License Org", false)),
                "/api/v1/organisations" => OrganisationsListResponse((50, "Acme Care", true)),
                "/api/v1/call_requests" => CreateInteractiveCallResponse(body!),
                "/api/v1/call_requests/request-2" => JsonResponse(HttpStatusCode.OK, """{"call_request":{"uuid":"request-2","organisation_id":50,"created_at":"2026-02-18T08:55:58.611Z","status":"completed","call_data":{"call_method":"phone","phone_number":"+61400000000","recipient_name":"Test User","recipient_timezone":"Australia/Melbourne"}}}"""),
                "/api/v1/call_results" => JsonResponse(HttpStatusCode.OK, """{"call_results":[{"uuid":"result-2","hotline_call_request_uuid":"request-2","organisation_id":50,"result_type":"outbound","results":{},"created_at":"2026-02-18T08:57:58.611Z","answered_at":"2026-02-18T08:57:59.611Z","status":"success","result":"answered"}]}"""),
                _ => throw new InvalidOperationException(request.RequestUri!.ToString())
            };
        }, interactiveUi: interactivePrompter, clock: clock);

        var exitCode = await harness.RunAsync("calls", "create", "--token", "inline-token", "--wait", "--output", "json");

        Assert.Equal(0, exitCode);
        Assert.True(interactivePrompter.LineCallCount > 0);
        Assert.Contains("Call mode [template/manual/template+manual]:", harness.StderrText);
        Assert.Empty(clock.Delays);
        Assert.Contains("\"state\": \"completed\"", harness.StdoutText);
        Assert.Contains("To make this call again execute: halley-cli calls create --organisation-id 50 --call-method phone --recipient-name 'Test User' --recipient-timezone Australia/Melbourne --phone-number +61400000000 --instructions 'Please check in'", harness.StderrText);
        Assert.DoesNotContain("--wait", harness.StderrText);
        Assert.DoesNotContain("--output", harness.StderrText);
        Assert.DoesNotContain("--token", harness.StderrText);
        Assert.Contains(interactivePrompter.PromptRequests, request => request.Prompt == "Organisation: " && request.Suggestions.Any(suggestion => suggestion.Value == "Acme Care"));
        Assert.Contains(interactivePrompter.PromptRequests, request => request.Prompt == "Call method [phone/web]: " && request.Suggestions.Any(suggestion => suggestion.Value == "phone"));
        Assert.Contains(interactivePrompter.PromptRequests, request => request.Prompt == "Recipient timezone: " && request.Suggestions.Any(suggestion => suggestion.Value == "Australia/Melbourne"));
        Assert.Contains(interactivePrompter.PromptRequests, request => request.Prompt == "Instructions" && request.IsMultiline && request.Suggestions.Count > 0);
    }

    [Fact]
    public async Task CallsCreateInteractivePromptReasksForInvalidPhoneAndTimezone()
    {
        var interactivePrompter = new StubInteractiveUi(
            isInteractive: true,
            lineResponses:
            [
                "manual",
                "Acme Care",
                "phone",
                "0400000000",
                "+61400000000",
                "Test User",
                "Eastern Standard Time",
                "Australia/Melbourne",
                "n",
                "n"
            ],
            multilineResponses:
            [
                "Please check in",
                string.Empty
            ]);

        using var harness = new TestHarness((request, body) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/organisations" when request.RequestUri!.Query.Contains("size=200", StringComparison.Ordinal) => OrganisationsListResponse((50, "Acme Care", true)),
                "/api/v1/organisations" => OrganisationsListResponse((50, "Acme Care", true)),
                "/api/v1/call_requests" => CreateInteractiveCallResponse(body!),
                _ => throw new InvalidOperationException(request.RequestUri!.ToString())
            };
        }, interactiveUi: interactivePrompter);

        var exitCode = await harness.RunAsync("calls", "create", "--token", "inline-token");

        Assert.Equal(0, exitCode);
        Assert.Equal(2, interactivePrompter.PromptRequests.Count(request => request.Prompt == "Phone number: "));
        Assert.Equal(2, interactivePrompter.PromptRequests.Count(request => request.Prompt == "Recipient timezone: "));
        Assert.Contains("Expected a valid international phone number such as `+61400000000`.", harness.StderrText);
        Assert.Contains("Expected a valid IANA timezone such as `Australia/Melbourne`.", harness.StderrText);
    }

    [Fact]
    public async Task CallsCreateFailsFastWhenOrganisationSuggestionsCannotBeLoaded()
    {
        var interactivePrompter = new StubInteractiveUi(
            isInteractive: true,
            lineResponses:
            [
                "manual"
            ]);

        using var harness = new TestHarness((request, _) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/organisations" => new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("<html>login</html>")
                },
                _ => throw new InvalidOperationException(request.RequestUri!.ToString())
            };
        }, interactiveUi: interactivePrompter);

        var exitCode = await harness.RunAsync("calls", "create", "--token", "inline-token");

        Assert.Equal(1, exitCode);
        Assert.Equal(1, interactivePrompter.LineCallCount);
        Assert.DoesNotContain(interactivePrompter.PromptRequests, request => request.Prompt == "Organisation: ");
        Assert.Contains("Authentication failed.", harness.StderrText);
        Assert.Contains("Run `login ...` again", harness.StderrText);
    }

    [Fact]
    public async Task CallsCreateWithoutOptionsFailsWhenConsoleIsNotInteractive()
    {
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.Created, """{}"""));

        var exitCode = await harness.RunAsync("calls", "create", "--token", "inline-token");

        Assert.Equal(1, exitCode);
        Assert.Contains("requires an interactive terminal", harness.StderrText);
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public async Task CallsCreateUsesStructuredWizardWhenRichUiIsAvailable()
    {
        var interactiveUi = new StubInteractiveUi(
            isInteractive: true,
            supportsCallCreateWizard: true,
            callCreateWizardResult: new InteractiveCallCreateResult(
                false,
                false,
                "Acme Care",
                "web",
                null,
                "Test User",
                "Australia/Melbourne",
                null,
                null,
                "Please check in",
                "Say hello",
                ["Warm handoff requested"],
                [new InteractiveCallCreateQuestion(1, "Was the resident okay?", "boolean")]));

        using var harness = new TestHarness((request, body) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/organisations" => OrganisationsListResponse((50, "Acme Care", true), (51, "No License Org", false)),
                "/api/v1/call_requests" => AssertWizardCallRequest(body!),
                _ => throw new InvalidOperationException(request.RequestUri!.ToString())
            };
        }, interactiveUi: interactiveUi);

        var exitCode = await harness.RunAsync("calls", "create", "--token", "inline-token", "--output", "json");

        Assert.Equal(0, exitCode);
        Assert.Equal(1, interactiveUi.WizardCallCount);
        Assert.Equal(0, interactiveUi.LineCallCount);
        Assert.Single(interactiveUi.WizardRequests);
        Assert.Contains(interactiveUi.WizardRequests[0].Organisations, suggestion => suggestion.Value == "Acme Care");
        Assert.Contains(interactiveUi.WizardRequests[0].Organisations, suggestion => suggestion.Value == "Acme Care" && suggestion.Description == "Hotline licensed");
        Assert.Contains(interactiveUi.WizardRequests[0].Organisations, suggestion => suggestion.Value == "No License Org" && suggestion.Description == "No Hotline license");
        Assert.DoesNotContain(interactiveUi.WizardRequests[0].Organisations, suggestion => suggestion.Description?.Contains("id ", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Equal("request-rich", JsonNode.Parse(harness.StdoutText)!["call_request"]?["uuid"]?.GetValue<string>());
        Assert.Contains("To make this call again execute: halley-cli calls create", harness.StderrText);
        Assert.Contains("--organisation-id 50", harness.StderrText);
        Assert.Contains("--call-method web", harness.StderrText);
        Assert.Contains("--recipient-name 'Test User'", harness.StderrText);
        Assert.Contains("--instructions 'Please check in'", harness.StderrText);
        Assert.Contains("--agenda 'Say hello'", harness.StderrText);
        Assert.Contains("--note 'Warm handoff requested'", harness.StderrText);
        Assert.Contains("Was the resident okay?", harness.StderrText);
    }

    [Fact]
    public async Task CallsCreateReplayHintQuotesSpecialValuesAndIncludesExplicitEndpoint()
    {
        var interactiveUi = new StubInteractiveUi(
            isInteractive: true,
            supportsCallCreateWizard: true,
            callCreateWizardResult: new InteractiveCallCreateResult(
                false,
                false,
                "Acme Care",
                "web",
                null,
                "Pat O'Brien",
                "Australia/Melbourne",
                "template-uuid",
                7,
                "Line one\nLine two",
                "Say \"hello\" & confirm",
                ["Resident's cat"],
                [new InteractiveCallCreateQuestion(1, "What's needed next?", "string")]));

        using var harness = new TestHarness((request, body) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/organisations" => OrganisationsListResponse((50, "Acme Care", true)),
                "/api/v1/call_templates/7" => CallTemplateResponse(7, "template-uuid", "Wellbeing Check", 3),
                "/api/v1/call_requests" => AssertReplayHintCallRequest(body!),
                _ => throw new InvalidOperationException(request.RequestUri!.ToString())
            };
        }, interactiveUi: interactiveUi);

        var exitCode = await harness.RunAsync(
            "calls",
            "create",
            "--token", "inline-token",
            "--endpoint", "https://cloud.blah.halleyassist.com",
            "--output", "json");

        Assert.Equal(0, exitCode);
        Assert.Equal("request-special", JsonNode.Parse(harness.StdoutText)!["call_request"]?["uuid"]?.GetValue<string>());
        Assert.Contains("To make this call again execute: halley-cli calls create", harness.StderrText);
        Assert.Contains("--organisation-id 50", harness.StderrText);
        Assert.Contains("--template-uuid template-uuid", harness.StderrText);
        Assert.Contains("--template-id 7", harness.StderrText);
        Assert.Contains("--endpoint https://cloud.blah.halleyassist.com", harness.StderrText);
        Assert.Contains("--recipient-name 'Pat O'\"'\"'Brien'", harness.StderrText);
        Assert.Contains("--instructions $'Line one\\nLine two'", harness.StderrText);
        Assert.Contains("--agenda 'Say \"hello\" & confirm'", harness.StderrText);
        Assert.Contains("--note 'Resident'\"'\"'s cat'", harness.StderrText);
        Assert.Contains("--question '1:string:What'\"'\"'s needed next?'", harness.StderrText);
        Assert.DoesNotContain("--output", harness.StderrText);
        Assert.DoesNotContain("--token", harness.StderrText);
        Assert.DoesNotContain("--wait", harness.StderrText);
        Assert.DoesNotContain("--delete", harness.StderrText);
        Assert.DoesNotContain("--poll-every", harness.StderrText);
        Assert.DoesNotContain("--timeout", harness.StderrText);
    }

    [Fact]
    public async Task CallsCreateWizardCanReturnTheCreateCommandWithoutCreatingTheCall()
    {
        var interactiveUi = new StubInteractiveUi(
            isInteractive: true,
            supportsCallCreateWizard: true,
            callCreateWizardResult: new InteractiveCallCreateResult(
                false,
                true,
                "Acme Care",
                "web",
                null,
                "Test User",
                "Australia/Melbourne",
                null,
                null,
                "Please check in",
                "Say hello",
                [],
                []));

        using var harness = new TestHarness((request, _) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/organisations" => OrganisationsListResponse((50, "Acme Care", true)),
                _ => throw new InvalidOperationException(request.RequestUri!.ToString())
            };
        }, interactiveUi: interactiveUi);

        var exitCode = await harness.RunAsync("calls", "create", "--token", "inline-token");

        Assert.Equal(0, exitCode);
        Assert.Equal("halley-cli calls create --organisation-id 50 --call-method web --recipient-name 'Test User' --recipient-timezone Australia/Melbourne --instructions 'Please check in' --agenda 'Say hello'", harness.StdoutText.Trim());
        Assert.DoesNotContain("To make this call again execute:", harness.StderrText);
        Assert.DoesNotContain(harness.Requests, request => request.Uri.Contains("/api/v1/call_requests", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CallsCreateWizardReturnsWindowsCompatibleReplayCommandUsingExecutableName()
    {
        var interactiveUi = new StubInteractiveUi(
            isInteractive: true,
            supportsCallCreateWizard: true,
            callCreateWizardResult: new InteractiveCallCreateResult(
                false,
                true,
                "Acme Care",
                "phone",
                "+61437635615",
                "Mathew Heard",
                "Australia/Melbourne",
                null,
                null,
                "You are collecting feedback on performance for an aged care home MatHealth",
                "Collect answers for all questions and then hang up the call",
                [],
                [new InteractiveCallCreateQuestion(1, "How do you feel about cake for lunch?", "string")]));

        using var harness = new TestHarness(
            (request, _) => request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/organisations" => OrganisationsListResponse((50, "Acme Care", true)),
                _ => throw new InvalidOperationException(request.RequestUri!.ToString())
            },
            interactiveUi: interactiveUi,
            replayCommandName: "Halley.App.Cli.exe",
            isWindows: true);

        var exitCode = await harness.RunAsync("calls", "create", "--token", "inline-token", "--endpoint", "https://cloud.dev.halleyassist.com");

        Assert.Equal(0, exitCode);
        Assert.Equal(
            "Halley.App.Cli.exe calls create --organisation-id 50 --call-method phone --recipient-name \"Mathew Heard\" --recipient-timezone Australia/Melbourne --phone-number +61437635615 --instructions \"You are collecting feedback on performance for an aged care home MatHealth\" --agenda \"Collect answers for all questions and then hang up the call\" --question \"1:string:How do you feel about cake for lunch?\" --endpoint https://cloud.dev.halleyassist.com",
            harness.StdoutText.Trim());
        Assert.DoesNotContain("'", harness.StdoutText);
    }

    [Fact]
    public async Task CallsCreateWizardRejectsOrganisationWithoutHotlineLicense()
    {
        var interactiveUi = new StubInteractiveUi(
            isInteractive: true,
            supportsCallCreateWizard: true,
            callCreateWizardResult: new InteractiveCallCreateResult(
                false,
                false,
                "No License Org",
                "web",
                null,
                "Test User",
                "Australia/Melbourne",
                null,
                null,
                "Please check in",
                "Say hello",
                [],
                []));

        using var harness = new TestHarness((request, _) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/organisations" => OrganisationsListResponse((50, "Acme Care", true), (51, "No License Org", false)),
                _ => throw new InvalidOperationException(request.RequestUri!.ToString())
            };
        }, interactiveUi: interactiveUi);

        var exitCode = await harness.RunAsync("calls", "create", "--token", "inline-token");

        Assert.Equal(1, exitCode);
        Assert.Contains("does not have an active Hotline license", harness.StderrText);
        Assert.DoesNotContain(harness.Requests, request => request.Uri.Contains("/api/v1/call_requests", StringComparison.Ordinal));
    }

    [Fact]
    public void CallCreateWizardTimezoneFieldTurnsRedOnBlurAndClearsWhenCorrected()
    {
        RunOnAvaloniaThread(() =>
        {
            var window = CreateCallCreateWizardWindow();
            var timezoneBox = GetPrivateField<AutoCompleteBox>(window, "_timezoneBox");
            var validationState = GetPrivateField(window, "_timezoneValidation");

            timezoneBox.Text = "Eastern Standard Time";
            InvokePrivateMethod(window, "ValidateFieldOnBlur", validationState);

            Assert.Contains("invalid", timezoneBox.Classes);
            var errorText = GetValidatedFieldErrorText(validationState);
            Assert.True(errorText.IsVisible);
            Assert.Equal("Expected a valid IANA timezone such as `Australia/Melbourne`.", errorText.Text);

            timezoneBox.Text = "Australia/Melbourne";
            InvokePrivateMethod(window, "HandleFieldTextChanged", validationState);

            Assert.DoesNotContain("invalid", timezoneBox.Classes);
            Assert.False(errorText.IsVisible);
            Assert.Equal(string.Empty, errorText.Text);
        });
    }

    [Fact]
    public void CallCreateWizardPhoneFieldTurnsRedAndClearsWhenSwitchingToWebMode()
    {
        RunOnAvaloniaThread(() =>
        {
            var window = CreateCallCreateWizardWindow();
            var phoneNumberBox = GetPrivateField<TextBox>(window, "_phoneNumberBox");
            var validationState = GetPrivateField(window, "_phoneNumberValidation");
            var webMethodButton = GetPrivateField<RadioButton>(window, "_webMethodButton");

            phoneNumberBox.Text = "0400000000";
            InvokePrivateMethod(window, "ValidateFieldOnBlur", validationState);

            Assert.Contains("invalid", phoneNumberBox.Classes);

            webMethodButton.IsChecked = true;
            InvokePrivateMethod(window, "UpdateVisibility");

            Assert.DoesNotContain("invalid", phoneNumberBox.Classes);
            Assert.False(GetValidatedFieldErrorText(validationState).IsVisible);
        });
    }

    [Fact]
    public void CallCreateWizardTemplateVersionTurnsRedWhenInvalidAndClearsWhenTemplateModeIsDisabled()
    {
        RunOnAvaloniaThread(() =>
        {
            var window = CreateCallCreateWizardWindow();
            var templateModeButton = GetPrivateField<RadioButton>(window, "_templateModeButton");
            var manualModeButton = GetPrivateField<RadioButton>(window, "_manualModeButton");
            var templateVersionBox = GetPrivateField<AutoCompleteBox>(window, "_templateVersionBox");
            var validationState = GetPrivateField(window, "_templateVersionValidation");

            templateModeButton.IsChecked = true;
            InvokePrivateMethod(window, "UpdateVisibility");

            templateVersionBox.Text = "zero";
            InvokePrivateMethod(window, "ValidateFieldOnBlur", validationState);

            Assert.Contains("invalid", templateVersionBox.Classes);
            Assert.Equal("Template version must be a positive integer.", GetValidatedFieldErrorText(validationState).Text);

            manualModeButton.IsChecked = true;
            InvokePrivateMethod(window, "UpdateVisibility");

            Assert.DoesNotContain("invalid", templateVersionBox.Classes);
            Assert.False(GetValidatedFieldErrorText(validationState).IsVisible);
        });
    }

    [Fact]
    public void CallCreateWizardQuestionDraftMarksInvalidFieldsOnStepSubmit()
    {
        RunOnAvaloniaThread(() =>
        {
            var window = CreateCallCreateWizardWindow();
            var questionIdBox = GetPrivateField<TextBox>(window, "_questionIdBox");
            var questionFormatBox = GetPrivateField<AutoCompleteBox>(window, "_questionFormatBox");
            var questionTextBox = GetPrivateField<TextBox>(window, "_questionTextBox");

            SetWizardStep(window, "NotesQuestions");
            questionFormatBox.Text = "boolean";
            questionTextBox.Text = "Was the resident okay?";

            InvokePrivateMethod(window, "MoveNext");

            Assert.Contains("invalid", questionIdBox.Classes);
            Assert.DoesNotContain("invalid", questionFormatBox.Classes);
            Assert.DoesNotContain("invalid", questionTextBox.Classes);
            Assert.Equal("Question ids must be positive integers.", GetPrivateField<TextBlock>(window, "_errorText").Text);
            Assert.Same(questionIdBox, GetPrivateField<Control>(window, "_lastValidationFocusTarget"));
        });
    }

    [Fact]
    public void CallCreateWizardMoveNextMarksAllInvalidSetupFieldsAndTracksFirstFocusTarget()
    {
        RunOnAvaloniaThread(() =>
        {
            var window = CreateCallCreateWizardWindow();
            var organisationBox = GetPrivateField<AutoCompleteBox>(window, "_organisationBox");
            var recipientNameBox = GetPrivateField<TextBox>(window, "_recipientNameBox");
            var timezoneBox = GetPrivateField<AutoCompleteBox>(window, "_timezoneBox");
            var phoneNumberBox = GetPrivateField<TextBox>(window, "_phoneNumberBox");

            InvokePrivateMethod(window, "MoveNext");

            Assert.Contains("invalid", organisationBox.Classes);
            Assert.Contains("invalid", recipientNameBox.Classes);
            Assert.Contains("invalid", timezoneBox.Classes);
            Assert.Contains("invalid", phoneNumberBox.Classes);
            Assert.Equal("An organisation is required.", GetPrivateField<TextBlock>(window, "_errorText").Text);
            Assert.Same(organisationBox, GetPrivateField<Control>(window, "_lastValidationFocusTarget"));
        });
    }

    [Fact]
    public async Task CallsCreateWithPartialOptionsDoesNotPrompt()
    {
        var interactivePrompter = new StubInteractiveUi(isInteractive: true, lineResponses: ["manual"]);
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.Created, """{}"""), interactiveUi: interactivePrompter);

        var exitCode = await harness.RunAsync(
            "calls",
            "create",
            "--organisation-id", "50",
            "--token", "inline-token");

        Assert.Equal(1, exitCode);
        Assert.Equal(0, interactivePrompter.LineCallCount);
        Assert.Contains("A valid --call-method value is required", harness.StderrText);
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public void InteractiveSuggestionMatcherPrefersPrefixMatchesOverContainsMatches()
    {
        var matches = InteractiveSuggestionMatcher.GetMatches(
            "m",
            [
                new InteractiveSuggestion("template"),
                new InteractiveSuggestion("manual"),
                new InteractiveSuggestion("template+manual")
            ]);

        Assert.Equal("manual", matches[0].Value);
        Assert.Equal("template+manual", matches[1].Value);
        Assert.Equal("template", matches[2].Value);
    }

    [Fact]
    public void InteractiveDialogShortcutsTreatCtrlCAsCancel()
    {
        var shortcutType = typeof(HalleyCliApplication).Assembly.GetType("Halley.App.Main.InteractiveDialogShortcuts");
        Assert.NotNull(shortcutType);

        var isCancelShortcut = shortcutType!.GetMethod(
            "IsCancelShortcut",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(isCancelShortcut);

        var ctrlC = (bool)isCancelShortcut!.Invoke(null, [Key.C, KeyModifiers.Control])!;
        var plainC = (bool)isCancelShortcut.Invoke(null, [Key.C, KeyModifiers.None])!;

        Assert.True(ctrlC);
        Assert.False(plainC);
    }

    [Theory]
    [InlineData("phone", null, "`--phone-number` is required when `--call-method phone` is used.")]
    [InlineData("web", "+61400000000", "`--phone-number` cannot be used when `--call-method web` is selected.")]
    public async Task CallsCreateValidatesPhoneInputs(string callMethod, string? phoneNumber, string expectedError)
    {
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.Created, """{}"""));

        var args = new List<string>
        {
            "calls",
            "create",
            "--organisation-id", "50",
            "--call-method", callMethod,
            "--recipient-name", "Test User",
            "--recipient-timezone", "Australia/Melbourne",
            "--instructions", "Please check in",
            "--token", "inline-token"
        };

        if (phoneNumber is not null)
        {
            args.Add("--phone-number");
            args.Add(phoneNumber);
        }

        var exitCode = await harness.RunAsync(args.ToArray());

        Assert.Equal(1, exitCode);
        Assert.Contains(expectedError, harness.StderrText);
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public async Task CallsCreateRejectsInvalidPhoneNumber()
    {
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.Created, """{}"""));

        var exitCode = await harness.RunAsync(
            "calls",
            "create",
            "--organisation-id", "50",
            "--call-method", "phone",
            "--phone-number", "0400000000",
            "--recipient-name", "Test User",
            "--recipient-timezone", "Australia/Melbourne",
            "--instructions", "Please check in",
            "--token", "inline-token");

        Assert.Equal(1, exitCode);
        Assert.Contains("Invalid --phone-number value. Expected a valid international phone number such as `+61400000000`.", harness.StderrText);
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public async Task CallsCreateRejectsInvalidRecipientTimezone()
    {
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.Created, """{}"""));

        var exitCode = await harness.RunAsync(
            "calls",
            "create",
            "--organisation-id", "50",
            "--call-method", "web",
            "--recipient-name", "Test User",
            "--recipient-timezone", "Eastern Standard Time",
            "--instructions", "Please check in",
            "--token", "inline-token");

        Assert.Equal(1, exitCode);
        Assert.Contains("Invalid --recipient-timezone value. Expected a valid IANA timezone such as `Australia/Melbourne`.", harness.StderrText);
        Assert.Empty(harness.Requests);
    }

    [Theory]
    [InlineData("ValidateIanaTimezone", "Australia/Melbourne", null)]
    [InlineData("ValidateIanaTimezone", "Eastern Standard Time", "Expected a valid IANA timezone such as `Australia/Melbourne`.")]
    [InlineData("ValidateInternationalPhoneNumber", "+61400000000", null)]
    [InlineData("ValidateInternationalPhoneNumber", "0400000000", "Expected a valid international phone number such as `+61400000000`.")]
    public void SharedApiFieldValidatorEnforcesExpectedRules(string methodName, string input, string? expectedError)
    {
        var validatorType = typeof(HalleyCliApplication).Assembly.GetType("Halley.App.Main.ApiFieldValidator");
        Assert.NotNull(validatorType);

        var method = validatorType!.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var error = (string?)method!.Invoke(null, [input]);
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public async Task CallsCreateRejectsTemplateIdWithoutTemplateUuid()
    {
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.Created, """{}"""));

        var exitCode = await harness.RunAsync(
            "calls",
            "create",
            "--organisation-id", "50",
            "--call-method", "web",
            "--recipient-name", "Test User",
            "--recipient-timezone", "Australia/Melbourne",
            "--template-id", "7",
            "--token", "inline-token");

        Assert.Equal(1, exitCode);
        Assert.Contains("`--template-id` requires `--template-uuid`.", harness.StderrText);
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public async Task CallsCreateRejectsMissingTemplateAndManualContent()
    {
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.Created, """{}"""));

        var exitCode = await harness.RunAsync(
            "calls",
            "create",
            "--organisation-id", "50",
            "--call-method", "web",
            "--recipient-name", "Test User",
            "--recipient-timezone", "Australia/Melbourne",
            "--note", "This alone is not enough",
            "--token", "inline-token");

        Assert.Equal(1, exitCode);
        Assert.Contains("Provide `--template-uuid` or at least one of", harness.StderrText);
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public async Task CallsCreateRejectsMalformedQuestion()
    {
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.Created, """{}"""));

        var exitCode = await harness.RunAsync(
            "calls",
            "create",
            "--organisation-id", "50",
            "--call-method", "web",
            "--recipient-name", "Test User",
            "--recipient-timezone", "Australia/Melbourne",
            "--instructions", "Please check in",
            "--question", "oops",
            "--token", "inline-token");

        Assert.Equal(1, exitCode);
        Assert.Contains("Invalid --question value `oops`", harness.StderrText);
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public async Task CallsStatusDerivesQueuedState()
    {
        using var harness = new TestHarness((request, _) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/call_requests/request-1" => JsonResponse(HttpStatusCode.OK, """{"call_request":{"uuid":"request-1","organisation_id":50,"created_at":"2026-02-18T08:55:58.611Z","status":"pending","call_data":{"call_method":"phone","phone_number":"+61400000000","recipient_name":"Test User","recipient_timezone":"Australia/Melbourne"}}}"""),
                "/api/v1/call_results" => JsonResponse(HttpStatusCode.OK, """{"call_results":[]}"""),
                _ => throw new InvalidOperationException(request.RequestUri!.ToString())
            };
        });

        var exitCode = await harness.RunAsync("calls", "status", "request-1", "--token", "inline-token", "--output", "json");

        Assert.Equal(0, exitCode);
        var payload = JsonNode.Parse(harness.StdoutText)!.AsObject();
        Assert.Equal("queued", payload["state"]?.GetValue<string>());
        Assert.Equal(0, payload["result_count"]?.GetValue<int>());
    }

    [Fact]
    public async Task CallsStatusWaitsUntilAResultExists()
    {
        var clock = new FakeAsyncClock();
        var resultRequests = 0;
        using var harness = new TestHarness((request, _) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/call_requests/request-1" => JsonResponse(HttpStatusCode.OK, """{"call_request":{"uuid":"request-1","organisation_id":50,"created_at":"2026-02-18T08:55:58.611Z","status":"active","call_data":{"call_method":"phone","phone_number":"+61400000000","recipient_name":"Test User","recipient_timezone":"Australia/Melbourne"}}}"""),
                "/api/v1/call_results" when resultRequests++ == 0 => JsonResponse(HttpStatusCode.OK, """{"call_results":[]}"""),
                "/api/v1/call_results" => JsonResponse(HttpStatusCode.OK, """{"call_results":[{"uuid":"result-1","hotline_call_request_uuid":"request-1","organisation_id":50,"result_type":"outbound","results":{},"created_at":"2026-02-18T08:57:58.611Z","answered_at":"2026-02-18T08:57:59.611Z","status":"success","result":"answered"}]}"""),
                _ => throw new InvalidOperationException(request.RequestUri!.ToString())
            };
        }, clock: clock);

        var exitCode = await harness.RunAsync(
            "calls",
            "status",
            "request-1",
            "--token", "inline-token",
            "--wait",
            "--poll-every", "5s",
            "--output", "json");

        Assert.Equal(0, exitCode);
        Assert.Single(clock.Delays);
        Assert.Equal(TimeSpan.FromSeconds(5), clock.Delays[0]);
        var payload = JsonNode.Parse(harness.StdoutText)!.AsObject();
        Assert.Equal("completed", payload["state"]?.GetValue<string>());
        Assert.False(payload["timed_out"]?.GetValue<bool>());
        Assert.True(payload["waited"]?.GetValue<bool>());
    }

    [Fact]
    public async Task CallsStatusTimeoutReturnsNonZeroAndPrintsSummary()
    {
        var clock = new FakeAsyncClock();
        using var harness = new TestHarness((request, _) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/call_requests/request-1" => JsonResponse(HttpStatusCode.OK, """{"call_request":{"uuid":"request-1","organisation_id":50,"created_at":"2026-02-18T08:55:58.611Z","status":"pending","call_data":{"call_method":"phone","recipient_name":"Test User","recipient_timezone":"Australia/Melbourne"}}}"""),
                "/api/v1/call_results" => JsonResponse(HttpStatusCode.OK, """{"call_results":[]}"""),
                _ => throw new InvalidOperationException(request.RequestUri!.ToString())
            };
        }, clock: clock);

        var exitCode = await harness.RunAsync(
            "calls",
            "status",
            "request-1",
            "--token", "inline-token",
            "--wait",
            "--poll-every", "5s",
            "--timeout", "10s",
            "--output", "json");

        Assert.Equal(1, exitCode);
        Assert.Equal(2, clock.Delays.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), clock.Delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(5), clock.Delays[1]);
        var payload = JsonNode.Parse(harness.StdoutText)!.AsObject();
        Assert.Equal("queued", payload["state"]?.GetValue<string>());
        Assert.True(payload["timed_out"]?.GetValue<bool>());
    }

    [Fact]
    public async Task CallsResultsUsesFilteredEndpointAndRendersHumanTable()
    {
        using var harness = new TestHarness((_, _) =>
            JsonResponse(HttpStatusCode.OK, """{"call_results":[{"uuid":"result-2","hotline_call_request_uuid":"request-1","organisation_id":50,"result_type":"outbound","results":{},"created_at":"2026-02-18T08:58:58.611Z","answered_at":"2026-02-18T08:58:59.611Z","status":"success","result":"answered"},{"uuid":"result-1","hotline_call_request_uuid":"request-1","organisation_id":50,"result_type":"outbound","results":{},"created_at":"2026-02-18T08:57:58.611Z","answered_at":"2026-02-18T08:57:59.611Z","status":"failed","result":"busy"}]}"""));

        var exitCode = await harness.RunAsync("calls", "results", "request-1", "--token", "inline-token");

        Assert.Equal(0, exitCode);
        Assert.Single(harness.Requests);
        Assert.Contains("/api/v1/call_results?", harness.Requests[0].Uri);
        Assert.Contains("hotline_call_request_uuid=request-1", harness.Requests[0].Uri);
        Assert.Contains("order=created_at DESC", harness.Requests[0].Uri);
        Assert.Contains("uuid", harness.StdoutText);
        Assert.Contains("result-2", harness.StdoutText);
        Assert.Contains("answered", harness.StdoutText);
        Assert.DoesNotContain("\"call_results\"", harness.StdoutText);
    }

    [Fact]
    public async Task CallsCreateWaitsForResultWhenRequested()
    {
        var clock = new FakeAsyncClock();
        var resultRequests = 0;
        using var harness = new TestHarness((request, body) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/organisations/50" => OrganisationResponse(),
                "/api/v1/call_requests" => JsonResponse(HttpStatusCode.Created, """{"call_request":{"uuid":"request-3","status":"pending"}}"""),
                "/api/v1/call_requests/request-3" => JsonResponse(HttpStatusCode.OK, """{"call_request":{"uuid":"request-3","organisation_id":50,"created_at":"2026-02-18T08:55:58.611Z","status":"active","call_data":{"call_method":"web","recipient_name":"Test User","recipient_timezone":"Australia/Melbourne"}}}"""),
                "/api/v1/call_results" when resultRequests++ == 0 => JsonResponse(HttpStatusCode.OK, """{"call_results":[]}"""),
                "/api/v1/call_results" => JsonResponse(HttpStatusCode.OK, """{"call_results":[{"uuid":"result-3","hotline_call_request_uuid":"request-3","organisation_id":50,"result_type":"outbound","results":{},"created_at":"2026-02-18T08:57:58.611Z","answered_at":"2026-02-18T08:57:59.611Z","status":"success","result":"answered"}]}"""),
                _ => throw new InvalidOperationException($"{request.RequestUri} {body}")
            };
        }, clock: clock);

        var exitCode = await harness.RunAsync(
            "calls",
            "create",
            "--organisation-id", "50",
            "--call-method", "web",
            "--recipient-name", "Test User",
            "--recipient-timezone", "Australia/Melbourne",
            "--instructions", "Please check in",
            "--token", "inline-token",
            "--wait",
            "--poll-every", "5s",
            "--output", "json");

        Assert.Equal(0, exitCode);
        Assert.Single(clock.Delays);
        Assert.Equal(TimeSpan.FromSeconds(5), clock.Delays[0]);
        var payload = JsonNode.Parse(harness.StdoutText)!.AsObject();
        Assert.Equal("request-3", payload["call_request_uuid"]?.GetValue<string>());
        Assert.Equal("completed", payload["state"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallsCreateAcceptsOrganisationNameArgument()
    {
        using var harness = new TestHarness((request, body) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/organisations" => OrganisationsListResponse((50, "Acme Care", true)),
                "/api/v1/call_requests" => AssertNamedOrganisationCallRequest(body!),
                _ => throw new InvalidOperationException(request.RequestUri!.ToString())
            };
        });

        var exitCode = await harness.RunAsync(
            "calls",
            "create",
            "--organisation", "Acme Care",
            "--call-method", "web",
            "--recipient-name", "Test User",
            "--recipient-timezone", "Australia/Melbourne",
            "--instructions", "Please check in",
            "--token", "inline-token",
            "--output", "json");

        Assert.Equal(0, exitCode);
        Assert.Equal("request-by-name", JsonNode.Parse(harness.StdoutText)!["call_request"]?["uuid"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallsCreateRejectsOrganisationWithoutHotlineLicense()
    {
        using var harness = new TestHarness((request, _) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/organisations/50" => OrganisationResponse(hotlineLicensed: false),
                _ => throw new InvalidOperationException(request.RequestUri!.ToString())
            };
        });

        var exitCode = await harness.RunAsync(
            "calls",
            "create",
            "--organisation-id", "50",
            "--call-method", "web",
            "--recipient-name", "Test User",
            "--recipient-timezone", "Australia/Melbourne",
            "--instructions", "Please check in",
            "--token", "inline-token");

        Assert.Equal(1, exitCode);
        Assert.Contains("does not have an active Hotline license", harness.StderrText);
        Assert.DoesNotContain(harness.Requests, request => request.Uri.Contains("/api/v1/call_requests", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CallsCreateRejectsUnknownTemplateVersionId()
    {
        using var harness = new TestHarness((request, _) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/organisations/50" => OrganisationResponse(),
                "/api/v1/call_templates/99" => new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"error":"missing"}""", Encoding.UTF8, "application/json")
                },
                _ => throw new InvalidOperationException(request.RequestUri!.ToString())
            };
        });

        var exitCode = await harness.RunAsync(
            "calls",
            "create",
            "--organisation-id", "50",
            "--call-method", "web",
            "--recipient-name", "Test User",
            "--recipient-timezone", "Australia/Melbourne",
            "--template-uuid", "template-uuid",
            "--template-id", "99",
            "--token", "inline-token");

        Assert.Equal(1, exitCode);
        Assert.Contains("does not have version id `99`", harness.StderrText);
        Assert.DoesNotContain(harness.Requests, request => request.Uri.Contains("/api/v1/call_requests", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CallsCreateDeleteWithoutResultWarnsAndReturnsStatusSummary()
    {
        using var harness = new TestHarness((request, _) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/organisations/50" => OrganisationResponse(),
                "/api/v1/call_requests" => JsonResponse(HttpStatusCode.Created, """{"call_request":{"uuid":"request-delete","status":"pending"}}"""),
                "/api/v1/call_requests/request-delete" => JsonResponse(HttpStatusCode.OK, """{"call_request":{"uuid":"request-delete","organisation_id":50,"created_at":"2026-02-18T08:55:58.611Z","status":"pending","call_data":{"call_method":"web","recipient_name":"Test User","recipient_timezone":"Australia/Melbourne"}}}"""),
                "/api/v1/call_results" => JsonResponse(HttpStatusCode.OK, """{"call_results":[]}"""),
                _ => throw new InvalidOperationException(request.RequestUri!.ToString())
            };
        });

        var exitCode = await harness.RunAsync(
            "calls",
            "create",
            "--organisation-id", "50",
            "--call-method", "web",
            "--recipient-name", "Test User",
            "--recipient-timezone", "Australia/Melbourne",
            "--instructions", "Please check in",
            "--token", "inline-token",
            "--delete",
            "--output", "json");

        Assert.Equal(0, exitCode);
        Assert.Contains("No call result was available to delete", harness.StderrText);
        var payload = JsonNode.Parse(harness.StdoutText)!.AsObject();
        Assert.Equal("queued", payload["state"]?.GetValue<string>());
        Assert.False(payload["deleted_call_result"]?.GetValue<bool>());
        Assert.DoesNotContain(harness.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task CallsStatusDeleteRemovesLatestResult()
    {
        using var harness = new TestHarness((request, _) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/call_requests/request-1" => JsonResponse(HttpStatusCode.OK, """{"call_request":{"uuid":"request-1","organisation_id":50,"created_at":"2026-02-18T08:55:58.611Z","status":"completed","call_data":{"call_method":"web","recipient_name":"Test User","recipient_timezone":"Australia/Melbourne"}}}"""),
                "/api/v1/call_results" => JsonResponse(HttpStatusCode.OK, """{"call_results":[{"uuid":"result-delete-1","hotline_call_request_uuid":"request-1","organisation_id":50,"result_type":"outbound","results":{},"created_at":"2026-02-18T08:57:58.611Z","answered_at":"2026-02-18T08:57:59.611Z","status":"success","result":"answered"}]}"""),
                "/api/v1/call_results/result-delete-1" => JsonResponse(HttpStatusCode.NoContent, string.Empty),
                _ => throw new InvalidOperationException(request.RequestUri!.ToString())
            };
        });

        var exitCode = await harness.RunAsync(
            "calls",
            "status",
            "request-1",
            "--token", "inline-token",
            "--delete",
            "--output", "json");

        Assert.Equal(0, exitCode);
        Assert.Contains(harness.Requests, request => request.Method == HttpMethod.Delete && request.Uri.EndsWith("/api/v1/call_results/result-delete-1", StringComparison.Ordinal));
        var payload = JsonNode.Parse(harness.StdoutText)!.AsObject();
        Assert.True(payload["deleted_call_result"]?.GetValue<bool>());
        Assert.Equal("result-delete-1", payload["deleted_call_result_uuid"]?.GetValue<string>());
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static object CreateCallCreateWizardWindow()
    {
        EnsureHeadlessAvalonia();

        var assembly = typeof(HalleyCliApplication).Assembly;
        var windowType = assembly.GetType("Halley.App.Main.CallCreateWizardWindow");
        Assert.NotNull(windowType);

        var completionType = assembly.GetType("Halley.App.Main.DialogCompletion`1");
        Assert.NotNull(completionType);
        var typedCompletion = completionType!.MakeGenericType(typeof(InteractiveCallCreateResult));
        var completion = Activator.CreateInstance(typedCompletion, nonPublic: true);
        Assert.NotNull(completion);

        var constructor = windowType!.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            [typeof(InteractiveCallCreateRequest), typedCompletion],
            modifiers: null);
        Assert.NotNull(constructor);

        return constructor!.Invoke([CreateWizardRequest(), completion!]);
    }

    private static InteractiveCallCreateRequest CreateWizardRequest() =>
        new(
            [
                new InteractiveSuggestion("Acme Care", "Hotline licensed"),
                new InteractiveSuggestion("No License Org", "No Hotline license")
            ],
            [
                new InteractiveSuggestion("Australia/Melbourne"),
                new InteractiveSuggestion("Europe/London")
            ],
            (_, _) => Task.FromResult<IReadOnlyList<InteractiveSuggestion>>([]),
            (_, _, _) => Task.FromResult<IReadOnlyList<InteractiveSuggestion>>([]));

    private static void EnsureHeadlessAvalonia()
    {
        lock (HeadlessLock)
        {
            if (_headlessInitialized)
            {
                return;
            }

            _headlessInitializedEvent = new AutoResetEvent(false);
            _headlessWorkAvailableEvent = new AutoResetEvent(false);
            _headlessWorkCompletedEvent = new AutoResetEvent(false);

            _headlessThread = new Thread(() =>
            {
                AppBuilder.Configure<HeadlessTestApplication>()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                    .SetupWithoutStarting();
                _headlessInitialized = true;
                _headlessInitializedEvent!.Set();

                while (true)
                {
                    _headlessWorkAvailableEvent!.WaitOne();

                    var action = _pendingHeadlessAction;
                    _pendingHeadlessAction = null;

                    try
                    {
                        action?.Invoke();
                        _pendingHeadlessException = null;
                    }
                    catch (Exception ex)
                    {
                        _pendingHeadlessException = ex;
                    }
                    finally
                    {
                        _headlessWorkCompletedEvent!.Set();
                    }
                }
            })
            {
                IsBackground = true,
                Name = "AvaloniaHeadlessTestThread"
            };
            _headlessThread.Start();
            _headlessInitializedEvent.WaitOne();
        }
    }

    private static void RunOnAvaloniaThread(Action action)
    {
        EnsureHeadlessAvalonia();
        _pendingHeadlessException = null;
        _pendingHeadlessAction = action;
        _headlessWorkAvailableEvent!.Set();
        _headlessWorkCompletedEvent!.WaitOne();
        if (_pendingHeadlessException is not null)
        {
            throw new TargetInvocationException(_pendingHeadlessException);
        }
    }

    private static void SetWizardStep(object window, string stepName)
    {
        var stepField = window.GetType().GetField("_currentStep", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(stepField);
        var stepType = stepField!.FieldType;
        stepField.SetValue(window, Enum.Parse(stepType, stepName));
    }

    private static object InvokePrivateMethod(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(target, args)!;
    }

    private static object GetPrivateField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(target)!;
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        var value = GetPrivateField(target, fieldName);
        return Assert.IsAssignableFrom<T>(value);
    }

    private static TextBlock GetValidatedFieldErrorText(object validationState)
    {
        var property = validationState.GetType().GetProperty("ErrorText", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        var value = property!.GetValue(validationState);
        Assert.IsType<TextBlock>(value);
        return (TextBlock)value;
    }

    private static HttpResponseMessage CreateInteractiveCallResponse(string body)
    {
        var callRequest = JsonNode.Parse(body)!.AsObject()["call_request"]!.AsObject();
        Assert.Equal("Please check in", callRequest["instructions"]?.GetValue<string>());
        Assert.Equal("+61400000000", callRequest["call_data"]?["phone_number"]?.GetValue<string>());
        return JsonResponse(HttpStatusCode.Created, """{"call_request":{"uuid":"request-2","status":"pending"}}""");
    }

    private static HttpResponseMessage AssertTemplateAndManualCallRequest(string body)
    {
        var callRequest = JsonNode.Parse(body)!.AsObject()["call_request"]!.AsObject();
        Assert.Equal(50, callRequest["organisation_id"]?.GetValue<int>());
        Assert.Equal("template-uuid", callRequest["hotline_call_template_uuid"]?.GetValue<string>());
        Assert.Equal(7, callRequest["hotline_call_template_id"]?.GetValue<int>());
        Assert.Equal("Please check in", callRequest["instructions"]?.GetValue<string>());
        Assert.Equal("Say hello", callRequest["agenda"]?.GetValue<string>());
        Assert.Equal("Weather was warm", callRequest["call_notes"]?[0]?.GetValue<string>());
        Assert.Equal(1, callRequest["result_questions"]?[0]?["id"]?.GetValue<int>());
        Assert.Equal("boolean", callRequest["result_questions"]?[0]?["format"]?.GetValue<string>());
        Assert.Equal("Was the resident okay?", callRequest["result_questions"]?[0]?["text"]?.GetValue<string>());
        Assert.Equal("phone", callRequest["call_data"]?["call_method"]?.GetValue<string>());
        Assert.Equal("+61400000000", callRequest["call_data"]?["phone_number"]?.GetValue<string>());
        Assert.Equal("Test User", callRequest["call_data"]?["recipient_name"]?.GetValue<string>());
        Assert.Equal("Australia/Melbourne", callRequest["call_data"]?["recipient_timezone"]?.GetValue<string>());
        return JsonResponse(HttpStatusCode.Created, """{"call_request":{"uuid":"request-1","status":"pending"}}""");
    }

    private static HttpResponseMessage AssertFileBackedCallRequest(string body)
    {
        var callRequest = JsonNode.Parse(body)!.AsObject()["call_request"]!.AsObject();
        Assert.Equal("Line one\nLine two", callRequest["instructions"]?.GetValue<string>());
        Assert.Equal("Agenda one", callRequest["agenda"]?.GetValue<string>());
        return JsonResponse(HttpStatusCode.Created, """{"call_request":{"uuid":"request-1"}}""");
    }

    private static HttpResponseMessage AssertNamedOrganisationCallRequest(string body)
    {
        var callRequest = JsonNode.Parse(body)!.AsObject()["call_request"]!.AsObject();
        Assert.Equal(50, callRequest["organisation_id"]?.GetValue<int>());
        Assert.Equal("Please check in", callRequest["instructions"]?.GetValue<string>());
        return JsonResponse(HttpStatusCode.Created, """{"call_request":{"uuid":"request-by-name","status":"pending"}}""");
    }

    private static HttpResponseMessage AssertActiveTemplateVersionCallRequest(string body)
    {
        var callRequest = JsonNode.Parse(body)!.AsObject()["call_request"]!.AsObject();
        Assert.Equal(50, callRequest["organisation_id"]?.GetValue<int>());
        Assert.Equal("template-uuid", callRequest["hotline_call_template_uuid"]?.GetValue<string>());
        Assert.Equal(7, callRequest["hotline_call_template_id"]?.GetValue<int>());
        return JsonResponse(HttpStatusCode.Created, """{"call_request":{"uuid":"request-active-template","status":"pending"}}""");
    }

    private static HttpResponseMessage AssertWizardCallRequest(string body)
    {
        var callRequest = JsonNode.Parse(body)!.AsObject()["call_request"]!.AsObject();
        Assert.Equal(50, callRequest["organisation_id"]?.GetValue<int>());
        Assert.Equal("Please check in", callRequest["instructions"]?.GetValue<string>());
        Assert.Equal("Say hello", callRequest["agenda"]?.GetValue<string>());
        Assert.Equal("Warm handoff requested", callRequest["call_notes"]?[0]?.GetValue<string>());
        Assert.Equal("web", callRequest["call_data"]?["call_method"]?.GetValue<string>());
        Assert.Equal("Test User", callRequest["call_data"]?["recipient_name"]?.GetValue<string>());
        Assert.Equal("Australia/Melbourne", callRequest["call_data"]?["recipient_timezone"]?.GetValue<string>());
        Assert.Equal(1, callRequest["result_questions"]?[0]?["id"]?.GetValue<int>());
        return JsonResponse(HttpStatusCode.Created, """{"call_request":{"uuid":"request-rich","status":"pending"}}""");
    }

    private static HttpResponseMessage AssertReplayHintCallRequest(string body)
    {
        var callRequest = JsonNode.Parse(body)!.AsObject()["call_request"]!.AsObject();
        Assert.Equal(50, callRequest["organisation_id"]?.GetValue<int>());
        Assert.Equal("template-uuid", callRequest["hotline_call_template_uuid"]?.GetValue<string>());
        Assert.Equal(7, callRequest["hotline_call_template_id"]?.GetValue<int>());
        Assert.Equal("Line one\nLine two", callRequest["instructions"]?.GetValue<string>());
        Assert.Equal("Say \"hello\" & confirm", callRequest["agenda"]?.GetValue<string>());
        Assert.Equal("Resident's cat", callRequest["call_notes"]?[0]?.GetValue<string>());
        Assert.Equal("Pat O'Brien", callRequest["call_data"]?["recipient_name"]?.GetValue<string>());
        Assert.Equal("What's needed next?", callRequest["result_questions"]?[0]?["text"]?.GetValue<string>());
        return JsonResponse(HttpStatusCode.Created, """{"call_request":{"uuid":"request-special","status":"pending"}}""");
    }

    private static HttpResponseMessage OrganisationResponse(int id = 50, string name = "Acme Care", bool hotlineLicensed = true) =>
        JsonResponse(HttpStatusCode.OK, new JsonObject
        {
            ["organisation"] = new JsonObject
            {
                ["id"] = id,
                ["name"] = name,
                ["active_license_hotline"] = hotlineLicensed
            }
        }.ToJsonString());

    private static HttpResponseMessage OrganisationsListResponse(params (int Id, string Name, bool HotlineLicensed)[] organisations) =>
        JsonResponse(HttpStatusCode.OK, new JsonObject
        {
            ["organisations"] = new JsonArray(
                organisations
                    .Select(organisation => (JsonNode)new JsonObject
                    {
                        ["id"] = organisation.Id,
                        ["name"] = organisation.Name,
                        ["active_license_hotline"] = organisation.HotlineLicensed
                    })
                    .ToArray())
        }.ToJsonString());

    private static HttpResponseMessage CallTemplatesResponse(params (int Id, string Uuid, string Name, int Version)[] templates) =>
        JsonResponse(HttpStatusCode.OK, new JsonObject
        {
            ["call_templates"] = new JsonArray(
                templates
                    .Select(template => (JsonNode)new JsonObject
                    {
                        ["id"] = template.Id,
                        ["uuid"] = template.Uuid,
                        ["name"] = template.Name,
                        ["version"] = template.Version
                    })
                    .ToArray())
        }.ToJsonString());

    private static HttpResponseMessage CallTemplateResponse(
        int id,
        string uuid,
        string name,
        int version) =>
        CallTemplateResponseWithVersions(id, (id, uuid, name, version));

    private static HttpResponseMessage CallTemplateResponseWithVersions(
        int selectedVersionId,
        params (int Id, string Uuid, string Name, int Version)[] templates) =>
        JsonResponse(HttpStatusCode.OK, new JsonObject
        {
            ["call_template"] = templates
                .Select(template => new JsonObject
                {
                    ["id"] = template.Id,
                    ["uuid"] = template.Uuid,
                    ["name"] = template.Name,
                    ["version"] = template.Version,
                    ["active"] = template.Id == selectedVersionId,
                    ["versions"] = new JsonArray(
                        templates
                            .OrderByDescending(versionInfo => versionInfo.Version)
                            .Select(versionInfo => (JsonNode)new JsonObject
                            {
                                ["id"] = versionInfo.Id,
                                ["version"] = versionInfo.Version,
                                ["active"] = versionInfo.Id == selectedVersionId,
                                ["created_at"] = "2026-02-18T08:55:58.611Z"
                            })
                            .ToArray())
                })
                .First(template => template["id"]?.GetValue<int>() == selectedVersionId)
        }.ToJsonString());

    private sealed class TestHarness : IDisposable
    {
        private readonly string _tempDirectory;
        private readonly RecordingHandler _handler;
        private readonly HttpClient _httpClient;
        private readonly StringWriter _stdout = new();
        private readonly StringWriter _stderr = new();

        public TestHarness(
            Func<HttpRequestMessage, string?, HttpResponseMessage> responder,
            IInteractiveUi? interactiveUi = null,
            ITextFileEditor? textFileEditor = null,
            IAsyncClock? clock = null,
            string replayCommandName = "halley-cli",
            bool isWindows = false)
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "halley-cli-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);

            SessionStore = new FileSessionStore(Path.Combine(_tempDirectory, "session.json"));
            _handler = new RecordingHandler(responder);
            _httpClient = new HttpClient(_handler);
            Application = new HalleyCliApplication(
                (options, logger) => new HalleyApiClient(_httpClient, options, logger),
                SessionStore,
                _stdout,
                _stderr,
                interactiveUi ?? new StubInteractiveUi(isInteractive: false),
                textFileEditor,
                clock,
                replayCommandNameProvider: () => replayCommandName,
                isWindowsProvider: () => isWindows);
        }

        public HalleyCliApplication Application { get; }

        public FileSessionStore SessionStore { get; }

        public string TempDirectory => _tempDirectory;

        public IReadOnlyList<RecordedRequest> Requests => _handler.Requests;

        public string StdoutText => _stdout.ToString();

        public string StderrText => _stderr.ToString();

        public Task<int> RunAsync(params string[] args) => Application.RunAsync(args);

        public void Dispose()
        {
            _httpClient.Dispose();
            _stdout.Dispose();
            _stderr.Dispose();

            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri?.ToString() ?? string.Empty,
                request.Headers.Authorization,
                body));

            return responder(request, body);
        }
    }

    private sealed class StubInteractiveUi(
        bool isInteractive,
        string? password = null,
        bool supportsCallCreateWizard = false,
        InteractiveCallCreateResult? callCreateWizardResult = null,
        IEnumerable<string?>? lineResponses = null,
        IEnumerable<string?>? multilineResponses = null) : IInteractiveUi
    {
        private readonly Queue<string?> _lineResponses = new(lineResponses ?? []);
        private readonly Queue<string?> _multilineResponses = new(multilineResponses ?? []);

        public bool IsInteractive { get; } = isInteractive;

        public bool SupportsCallCreateWizard { get; } = supportsCallCreateWizard;

        public int PasswordCallCount { get; private set; }

        public int LineCallCount { get; private set; }

        public int MultilineCallCount { get; private set; }

        public int WizardCallCount { get; private set; }

        public List<PromptRequest> PromptRequests { get; } = [];

        public List<InteractiveCallCreateRequest> WizardRequests { get; } = [];

        public Task<string?> ReadPasswordAsync(TextWriter output, CancellationToken cancellationToken = default)
        {
            PasswordCallCount++;
            return Task.FromResult(password);
        }

        public async Task<string?> ReadLineAsync(
            TextWriter output,
            string prompt,
            IReadOnlyList<InteractiveSuggestion>? suggestions = null,
            string? helpText = null,
            CancellationToken cancellationToken = default)
        {
            LineCallCount++;
            PromptRequests.Add(new PromptRequest(prompt, suggestions ?? [], helpText, IsMultiline: false));
            await output.WriteAsync(prompt.AsMemory(), cancellationToken);
            return _lineResponses.Count > 0 ? _lineResponses.Dequeue() : null;
        }

        public async Task<string?> ReadMultilineAsync(
            TextWriter output,
            string prompt,
            IReadOnlyList<InteractiveSuggestion>? suggestions = null,
            string? helpText = null,
            CancellationToken cancellationToken = default)
        {
            MultilineCallCount++;
            PromptRequests.Add(new PromptRequest(prompt, suggestions ?? [], helpText, IsMultiline: true));
            await output.WriteLineAsync(prompt.AsMemory(), cancellationToken);
            return _multilineResponses.Count > 0 ? _multilineResponses.Dequeue() : null;
        }

        public Task<InteractiveCallCreateResult> RunCallCreateWizardAsync(
            InteractiveCallCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            WizardCallCount++;
            WizardRequests.Add(request);
            if (!SupportsCallCreateWizard)
            {
                throw new NotSupportedException();
            }

            return Task.FromResult(callCreateWizardResult ?? InteractiveCallCreateResult.CancelledResult());
        }
    }

    private sealed class FakeAsyncClock : IAsyncClock
    {
        private DateTimeOffset _current;

        public FakeAsyncClock()
            : this(new DateTimeOffset(2026, 2, 18, 18, 55, 58, TimeSpan.FromHours(10)))
        {
        }

        public FakeAsyncClock(DateTimeOffset current)
        {
            _current = current;
        }

        public DateTimeOffset Now => _current;

        public DateTimeOffset UtcNow => _current.ToUniversalTime();
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            Delays.Add(delay);
            _current += delay;
            return Task.CompletedTask;
        }
    }

    private sealed class StubTextFileEditor : ITextFileEditor
    {
        public List<string> OpenedPaths { get; } = [];

        public void Open(string path)
        {
            OpenedPaths.Add(path);
        }
    }

    private static string DefaultSessionKey => HalleyEndpointResolver.Resolve(HalleyApiClientOptions.DefaultEndpoint).SessionKey;

    private static int CountOccurrences(string text, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string CreateJwt(DateTimeOffset expiresAtUtc)
    {
        static string Encode(object value)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(value);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        return $"{Encode(new { alg = "none", typ = "JWT" })}.{Encode(new { exp = expiresAtUtc.ToUnixTimeSeconds() })}.signature";
    }

    private sealed record PromptRequest(string Prompt, IReadOnlyList<InteractiveSuggestion> Suggestions, string? HelpText, bool IsMultiline);

    private sealed record RecordedRequest(HttpMethod Method, string Uri, AuthenticationHeaderValue? Authorization, string? Body);

    private sealed class HeadlessTestApplication : Application;
}
