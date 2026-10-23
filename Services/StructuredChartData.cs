using System.Text.Json;

namespace Jarvis.Services;

/// <summary>Strict, renderer-neutral payload for fenced <c>chart</c> blocks.</summary>
public sealed record StructuredChartData(string Title, IReadOnlyList<string> Labels, IReadOnlyList<double> Values)
{
    public static bool TryParse(string? json, out StructuredChartData? chart)
    {
        chart = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 12_000) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                !string.Equals(type.GetString(), "bar", StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("labels", out var labelsNode) || labelsNode.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("values", out var valuesNode) || valuesNode.ValueKind != JsonValueKind.Array ||
                labelsNode.GetArrayLength() is < 1 or > 12 || labelsNode.GetArrayLength() != valuesNode.GetArrayLength()) return false;

            var labels = new List<string>();
            foreach (var label in labelsNode.EnumerateArray())
            {
                if (label.ValueKind != JsonValueKind.String) return false;
                var value = label.GetString()?.Trim() ?? "";
                if (value.Length is < 1 or > 80 || value.Any(char.IsControl)) return false;
                labels.Add(value);
            }
            var values = new List<double>();
            foreach (var value in valuesNode.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)) return false;
                values.Add(number);
            }
            var title = root.TryGetProperty("title", out var titleNode) && titleNode.ValueKind == JsonValueKind.String
                ? titleNode.GetString()?.Trim() ?? "" : "";
            if (title.Length > 160 || title.Any(char.IsControl)) return false;
            chart = new StructuredChartData(title, labels, values);
            return true;
        }
        catch (JsonException) { return false; }
    }
}
