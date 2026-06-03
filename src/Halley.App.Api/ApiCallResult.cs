using System.Net;
using System.Text.Json.Nodes;

namespace Halley.App.Api;

public sealed record ApiCallResult(HttpStatusCode StatusCode, JsonNode? JsonBody, string? RawBody)
{
    public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and <= 299;
}
