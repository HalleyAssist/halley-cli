using System.Net;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Halley.App.Api;

namespace Halley.App.Main;

public sealed class HalleyOutputFormatter
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions CompactJsonOptions = new() { WriteIndented = false };

    public string FormatSuccess(CommandOutput output, OutputMode mode)
    {
        if (mode == OutputMode.Json)
        {
            return FormatJsonSuccess(output);
        }

        return output.Kind switch
        {
            CommandOutputKind.Token => output.Token ?? string.Empty,
            CommandOutputKind.Version => $"Version: {output.Version}{Environment.NewLine}Git SHA: {output.GitSha}",
            CommandOutputKind.Empty => string.Empty,
            CommandOutputKind.JsonPayload when output.HumanPayload is not null => FormatHumanPayload(output.HumanPayload),
            CommandOutputKind.JsonPayload when output.JsonPayload is not null => FormatHumanPayload(output.JsonPayload),
            _ => string.Empty
        };
    }

    public string FormatApiError(ApiCallResult result, OutputMode mode)
    {
        if (mode == OutputMode.Json)
        {
            return FormatJsonError(result);
        }

        var summary = ExtractErrorSummary(result.JsonBody);
        if (result.StatusCode == HttpStatusCode.Unauthorized)
        {
            var message = "Authentication failed. Run `login ...` again or pass a fresh `--token`.";
            return string.IsNullOrWhiteSpace(summary)
                ? $"{message}{Environment.NewLine}Request failed: {(int)result.StatusCode} {result.StatusCode}"
                : $"{message}{Environment.NewLine}Request failed: {(int)result.StatusCode} {result.StatusCode}{Environment.NewLine}Error: {summary}";
        }

        return string.IsNullOrWhiteSpace(summary)
            ? $"Request failed: {(int)result.StatusCode} {result.StatusCode}"
            : $"Request failed: {(int)result.StatusCode} {result.StatusCode}{Environment.NewLine}Error: {summary}";
    }

    public string FormatCliError(string message, OutputMode mode)
    {
        if (mode == OutputMode.Json)
        {
            return new JsonObject
            {
                ["error"] = message
            }.ToJsonString(PrettyJsonOptions);
        }

        return message;
    }

    private string FormatJsonSuccess(CommandOutput output)
    {
        return output.Kind switch
        {
            CommandOutputKind.Token => new JsonObject { ["token"] = output.Token }.ToJsonString(PrettyJsonOptions),
            CommandOutputKind.Version => new JsonObject
            {
                ["version"] = output.Version,
                ["gitSha"] = output.GitSha
            }.ToJsonString(PrettyJsonOptions),
            CommandOutputKind.Empty => "null",
            CommandOutputKind.JsonPayload when output.JsonPayload is not null => output.JsonPayload.ToJsonString(PrettyJsonOptions),
            _ => "null"
        };
    }

    private string FormatJsonError(ApiCallResult result)
    {
        if (result.JsonBody is not null)
        {
            return result.JsonBody.ToJsonString(PrettyJsonOptions);
        }

        return new JsonObject
        {
            ["status"] = (int)result.StatusCode,
            ["reason"] = result.StatusCode.ToString(),
            ["error"] = result.RawBody ?? "Request failed."
        }.ToJsonString(PrettyJsonOptions);
    }

    private string FormatHumanPayload(JsonNode payload)
    {
        if (payload is JsonObject objectPayload && objectPayload.Count == 1)
        {
            var property = objectPayload.First();
            if (property.Value is JsonArray arrayValue)
            {
                return FormatArrayTable(arrayValue);
            }

            if (property.Value is JsonObject nestedObject)
            {
                return FormatFieldTable(nestedObject);
            }
        }

        if (payload is JsonArray arrayPayload)
        {
            return FormatArrayTable(arrayPayload);
        }

        if (payload is JsonObject flatObject)
        {
            return FormatFieldTable(flatObject);
        }

        return FormatCellValue(payload);
    }

    private string FormatArrayTable(JsonArray arrayPayload)
    {
        if (arrayPayload.Count == 0)
        {
            return "No results.";
        }

        if (arrayPayload.All(item => item is JsonObject))
        {
            var columns = new List<string>();
            var widths = new Dictionary<string, int>(StringComparer.Ordinal);
            var rows = new List<Dictionary<string, string>>();

            foreach (var item in arrayPayload)
            {
                var objectItem = (JsonObject?)item ?? new JsonObject();
                var row = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var property in objectItem)
                {
                    if (!columns.Contains(property.Key, StringComparer.Ordinal))
                    {
                        columns.Add(property.Key);
                        widths[property.Key] = property.Key.Length;
                    }

                    var value = FormatCellValue(property.Value);
                    row[property.Key] = value;
                    widths[property.Key] = Math.Max(widths[property.Key], value.Length);
                }

                rows.Add(row);
            }

            return BuildTable(
                columns,
                rows.Select(row => columns.Select(column => row.TryGetValue(column, out var value) ? value : string.Empty).ToArray()).ToList(),
                widths);
        }

        var valueColumn = "Value";
        var valueRows = arrayPayload.Select(item => new[] { FormatCellValue(item) }).ToList();
        var valueWidths = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [valueColumn] = Math.Max(valueColumn.Length, valueRows.Max(row => row[0].Length))
        };

        return BuildTable([valueColumn], valueRows, valueWidths);
    }

    private string FormatFieldTable(JsonObject objectPayload)
    {
        var rows = objectPayload
            .Select(property => new[] { property.Key, FormatCellValue(property.Value) })
            .ToList();

        var widths = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Field"] = Math.Max("Field".Length, rows.Count == 0 ? 0 : rows.Max(row => row[0].Length)),
            ["Value"] = Math.Max("Value".Length, rows.Count == 0 ? 0 : rows.Max(row => row[1].Length))
        };

        return BuildTable(["Field", "Value"], rows, widths);
    }

    private static string BuildTable(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows, IReadOnlyDictionary<string, int> widths)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(" | ", headers.Select(header => header.PadRight(widths[header], ' '))));
        builder.AppendLine(string.Join("-+-", headers.Select(header => new string('-', widths[header]))));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(" | ", headers.Select((header, index) => row[index].PadRight(widths[header], ' '))));
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatCellValue(JsonNode? value)
    {
        return value switch
        {
            null => "null",
            JsonValue jsonValue when jsonValue.TryGetValue<string>(out var stringValue) => EscapeMultiline(stringValue),
            JsonValue jsonValue when jsonValue.TryGetValue<bool>(out var boolValue) => boolValue ? "true" : "false",
            JsonValue jsonValue when jsonValue.TryGetValue<double>(out var doubleValue) => doubleValue.ToString(CultureInfo.InvariantCulture),
            JsonValue jsonValue => EscapeMultiline(jsonValue.ToJsonString(CompactJsonOptions)),
            _ => EscapeMultiline(value.ToJsonString(CompactJsonOptions))
        };
    }

    private static string EscapeMultiline(string value) => value.ReplaceLineEndings("\\n");

    private static string? ExtractErrorSummary(JsonNode? payload)
    {
        if (payload is JsonObject objectPayload)
        {
            if (objectPayload["error"] is JsonNode errorNode)
            {
                return FormatCellValue(errorNode);
            }

            if (objectPayload["message"] is JsonNode messageNode)
            {
                return FormatCellValue(messageNode);
            }

            return payload.ToJsonString(CompactJsonOptions);
        }

        return payload is null ? null : FormatCellValue(payload);
    }
}
