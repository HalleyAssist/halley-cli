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

        var session = await harness.SessionStore.LoadAsync();
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

        var session = await harness.SessionStore.LoadAsync();
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

        await harness.SessionStore.SaveAsync(new SessionRecord("stored-token", "user", DateTimeOffset.UtcNow));

        var exitCode = await harness.RunAsync("users", "me");

        Assert.Equal(0, exitCode);
        Assert.Contains("Field", harness.StdoutText);
        Assert.Contains("alice", harness.StdoutText);
        Assert.DoesNotContain("{", harness.StdoutText);
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
    public async Task UsersPatchDefaultsNameToTargetWhenNewNameIsOmitted()
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
            "patch",
            "alice",
            "--password", "pw123",
            "--token", "inline-token");

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task UsersDeletePrintsNullInJsonModeForNoContent()
    {
        using var harness = new TestHarness((request, _) =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal("https://cloud.halleyassist.com/api/v1/users/alice", request.RequestUri?.ToString());
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        var exitCode = await harness.RunAsync("users", "delete", "alice", "--token", "inline-token", "--output", "json");

        Assert.Equal(0, exitCode);
        Assert.Equal("null", harness.StdoutText.Trim());
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

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class TestHarness : IDisposable
    {
        private readonly string _tempDirectory;
        private readonly RecordingHandler _handler;
        private readonly HttpClient _httpClient;
        private readonly StringWriter _stdout = new();
        private readonly StringWriter _stderr = new();

        public TestHarness(Func<HttpRequestMessage, string?, HttpResponseMessage> responder, IPasswordPrompt? passwordPrompt = null)
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
                passwordPrompt);
        }

        public HalleyCliApplication Application { get; }

        public FileSessionStore SessionStore { get; }

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

    private sealed record RecordedRequest(HttpMethod Method, string Uri, AuthenticationHeaderValue? Authorization, string? Body);
}
