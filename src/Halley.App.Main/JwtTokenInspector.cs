using System.Text.Json;

namespace Halley.App.Main;

internal static class JwtTokenInspector
{
    public static bool TryGetExpirationUtc(string token, out DateTimeOffset? expiresAtUtc)
    {
        expiresAtUtc = null;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var segments = token.Split('.');
        if (segments.Length < 2)
        {
            return false;
        }

        try
        {
            // JWT payloads use base64url rather than regular base64 encoding.
            var payloadBytes = DecodeBase64Url(segments[1]);
            using var payload = JsonDocument.Parse(payloadBytes);
            if (!payload.RootElement.TryGetProperty("exp", out var expElement))
            {
                return false;
            }

            long unixSeconds;
            switch (expElement.ValueKind)
            {
                case JsonValueKind.Number when expElement.TryGetInt64(out var intValue):
                    unixSeconds = intValue;
                    break;
                case JsonValueKind.Number:
                    unixSeconds = Convert.ToInt64(expElement.GetDouble());
                    break;
                default:
                    return false;
            }

            expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        var padding = base64.Length % 4;
        if (padding > 0)
        {
            base64 = base64.PadRight(base64.Length + (4 - padding), '=');
        }

        return Convert.FromBase64String(base64);
    }
}
