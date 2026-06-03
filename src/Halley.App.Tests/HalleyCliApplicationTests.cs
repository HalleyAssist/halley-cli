using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Halley.App.Api;
using Halley.App.Main;

namespace Halley.App.Tests;

public sealed class HalleyCliApplicationTests
{
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
    public async Task LoginUserPromptsForPasswordWhenOptionIsMissing()
    {
        var passwordPrompt = new StubPasswordPrompt("prompt-secret");
        using var harness = new TestHarness((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://cloud.halleyassist.com/login", request.RequestUri?.ToString());

            var payload = JsonNode.Parse(body!)!.AsObject();
            Assert.Equal("alice", payload["username"]?.GetValue<string>());
            Assert.Equal("prompt-secret", payload["password"]?.GetValue<string>());

            return JsonResponse(HttpStatusCode.Created, """{"token":"user-token"}""");
        }, passwordPrompt);

        var exitCode = await harness.RunAsync("login", "user", "--username", "alice");

        Assert.Equal(0, exitCode);
        Assert.Equal(1, passwordPrompt.CallCount);
        Assert.Equal("user-token", harness.StdoutText.Trim());
    }

    [Fact]
    public async Task LoginUserReturnsCliErrorWhenPromptedPasswordIsEmpty()
    {
        var passwordPrompt = new StubPasswordPrompt(string.Empty);
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.Created, """{"token":"unused"}"""), passwordPrompt);

        var exitCode = await harness.RunAsync("login", "user", "--username", "alice");

        Assert.Equal(1, exitCode);
        Assert.Equal(1, passwordPrompt.CallCount);
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
                "/api/v1/call_templates" => CallTemplatesResponse((7, "template-uuid", "Wellbeing Check", 3)),
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
        var interactivePrompter = new StubInteractivePrompter(
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
        }, interactivePrompter: interactivePrompter, clock: clock);

        var exitCode = await harness.RunAsync("calls", "create", "--token", "inline-token", "--wait", "--output", "json");

        Assert.Equal(0, exitCode);
        Assert.True(interactivePrompter.LineCallCount > 0);
        Assert.Contains("Call mode [template/manual/template+manual]:", harness.StderrText);
        Assert.Empty(clock.Delays);
        Assert.Contains("\"state\": \"completed\"", harness.StdoutText);
        Assert.Contains(interactivePrompter.PromptRequests, request => request.Prompt == "Organisation: " && request.Suggestions.Any(suggestion => suggestion.Value == "Acme Care"));
        Assert.Contains(interactivePrompter.PromptRequests, request => request.Prompt == "Call method [phone/web]: " && request.Suggestions.Any(suggestion => suggestion.Value == "phone"));
        Assert.Contains(interactivePrompter.PromptRequests, request => request.Prompt == "Recipient timezone: " && request.Suggestions.Any(suggestion => suggestion.Value == "Australia/Melbourne"));
        Assert.Contains(interactivePrompter.PromptRequests, request => request.Prompt == "Instructions" && request.IsMultiline && request.Suggestions.Count > 0);
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
    public async Task CallsCreateWithPartialOptionsDoesNotPrompt()
    {
        var interactivePrompter = new StubInteractivePrompter(isInteractive: true, lineResponses: ["manual"]);
        using var harness = new TestHarness((_, _) => JsonResponse(HttpStatusCode.Created, """{}"""), interactivePrompter: interactivePrompter);

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
                "/api/v1/call_templates" => CallTemplatesResponse((7, "template-uuid", "Wellbeing Check", 3)),
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

    private sealed class TestHarness : IDisposable
    {
        private readonly string _tempDirectory;
        private readonly RecordingHandler _handler;
        private readonly HttpClient _httpClient;
        private readonly StringWriter _stdout = new();
        private readonly StringWriter _stderr = new();

        public TestHarness(
            Func<HttpRequestMessage, string?, HttpResponseMessage> responder,
            IPasswordPrompt? passwordPrompt = null,
            IInteractivePrompter? interactivePrompter = null,
            IAsyncClock? clock = null)
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
                passwordPrompt,
                interactivePrompter ?? new StubInteractivePrompter(isInteractive: false),
                clock);
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

    private sealed class StubPasswordPrompt(string? password) : IPasswordPrompt
    {
        public int CallCount { get; private set; }

        public Task<string?> ReadPasswordAsync(TextWriter output, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(password);
        }
    }

    private sealed class StubInteractivePrompter(
        bool isInteractive,
        IEnumerable<string?>? lineResponses = null,
        IEnumerable<string?>? multilineResponses = null) : IInteractivePrompter
    {
        private readonly Queue<string?> _lineResponses = new(lineResponses ?? []);
        private readonly Queue<string?> _multilineResponses = new(multilineResponses ?? []);

        public bool IsInteractive { get; } = isInteractive;

        public int LineCallCount { get; private set; }

        public int MultilineCallCount { get; private set; }

        public List<PromptRequest> PromptRequests { get; } = [];

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
    }

    private sealed class FakeAsyncClock : IAsyncClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 2, 18, 8, 55, 58, TimeSpan.Zero);

        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            Delays.Add(delay);
            UtcNow += delay;
            return Task.CompletedTask;
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

    private sealed record PromptRequest(string Prompt, IReadOnlyList<InteractiveSuggestion> Suggestions, string? HelpText, bool IsMultiline);

    private sealed record RecordedRequest(HttpMethod Method, string Uri, AuthenticationHeaderValue? Authorization, string? Body);
}
