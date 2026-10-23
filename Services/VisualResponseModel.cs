using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO;

namespace Jarvis.Services;

public sealed record VisualResponseDocument(int SchemaVersion, IReadOnlyList<VisualBlock> Blocks);
public sealed record VisualBlock(string Type, string Title, string? Subtitle, IReadOnlyList<string> Categories,
    IReadOnlyList<VisualSeries> Series, IReadOnlyList<VisualMetric> Metrics, IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows, double? Value, double? Maximum, string? Unit,
    IReadOnlyList<WebImageResult> Images, IReadOnlyList<VisualStep> Steps);
public sealed record VisualSeries(string Name, IReadOnlyList<double> Values, string? Color);
public sealed record VisualMetric(string Label, double Value, string? Unit, double? Change);
public sealed record VisualStep(string Label, string? Detail, string? Status);
public sealed record VisualResponseSegment(string? Text, VisualResponseDocument? Visual);

public static partial class VisualResponseParser
{
    private static readonly HashSet<string> Types = new(StringComparer.OrdinalIgnoreCase)
    { "bar", "line", "pie", "donut", "area", "stackedBar", "kpi", "table", "progress", "gallery", "timeline", "comparison", "ranking" };

    [GeneratedRegex("```jarvis-visual\\s*(?<json>\\{[\\s\\S]*?\\})\\s*```", RegexOptions.IgnoreCase)]
    private static partial Regex VisualFence();

    public static IReadOnlyList<VisualResponseSegment> ParseSegments(string response)
    {
        var segments = new List<VisualResponseSegment>(); var offset = 0;
        foreach (Match match in VisualFence().Matches(response))
        {
            if (match.Index > offset) segments.Add(new(response[offset..match.Index], null));
            try { segments.Add(new(null, Parse(match.Groups["json"].Value))); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or KeyNotFoundException) { segments.Add(new("⚠️ Visualisierung konnte nicht dargestellt werden: " + ex.Message, null)); }
            offset = match.Index + match.Length;
        }
        if (offset < response.Length) segments.Add(new(response[offset..], null));
        return segments.Count == 0 ? [new(response, null)] : segments;
    }

    public static VisualResponseDocument Parse(string json)
    {
        if (json.Length > 256_000) throw new InvalidDataException("Visualisierungsdaten sind zu groß.");
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16, AllowTrailingCommas = false });
        var root = doc.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var version) || version.GetInt32() != 1) throw new InvalidDataException("Nicht unterstützte Visualisierungs-Version.");
        if (!root.TryGetProperty("blocks", out var blocksNode) || blocksNode.ValueKind != JsonValueKind.Array || blocksNode.GetArrayLength() is < 1 or > 12) throw new InvalidDataException("Es werden 1–12 Visualisierungsblöcke benötigt.");
        var blocks = blocksNode.EnumerateArray().Select(ParseBlock).ToList();
        return new(1, blocks);
    }

    private static VisualBlock ParseBlock(JsonElement node)
    {
        var type = Required(node, "type"); if (!Types.Contains(type)) throw new InvalidDataException($"Unbekannter Visualisierungstyp: {type}");
        var title = Required(node, "title", 120); var subtitle = Optional(node, "subtitle", 240);
        var categories = Strings(node, "categories", 50, 80); var series = Series(node); var metrics = Metrics(node);
        var columns = Strings(node, "columns", 16, 80); var rows = Rows(node, columns.Count);
        var value = Number(node, "value"); var maximum = Number(node, "maximum"); var unit = Optional(node, "unit", 24);
        var images = Images(node); var steps = Steps(node);
        if (new[] { "bar", "line", "area", "stackedBar", "pie", "donut", "ranking" }.Contains(type, StringComparer.OrdinalIgnoreCase))
        {
            if (series.Count == 0 || series.Any(s => s.Values.Count == 0 || s.Values.Any(v => !double.IsFinite(v)))) throw new InvalidDataException("Diagrammdaten fehlen oder enthalten ungültige Zahlen.");
            if (categories.Count > 0 && series.Any(s => s.Values.Count != categories.Count)) throw new InvalidDataException("Kategorien und Werte haben unterschiedliche Längen.");
        }
        if (type.Equals("table", StringComparison.OrdinalIgnoreCase) || type.Equals("comparison", StringComparison.OrdinalIgnoreCase))
            if (columns.Count == 0 || rows.Count == 0) throw new InvalidDataException("Tabellenspalten oder Zeilen fehlen.");
        if (type.Equals("kpi", StringComparison.OrdinalIgnoreCase) && metrics.Count == 0) throw new InvalidDataException("Kennzahlen fehlen.");
        if (type.Equals("progress", StringComparison.OrdinalIgnoreCase) && (value is null || maximum is null || maximum <= 0 || value < 0 || value > maximum)) throw new InvalidDataException("Der Fortschrittswert ist ungültig.");
        if (type.Equals("gallery", StringComparison.OrdinalIgnoreCase) && images.Count == 0) throw new InvalidDataException("Galeriebilder fehlen.");
        if (type.Equals("timeline", StringComparison.OrdinalIgnoreCase) && steps.Count == 0) throw new InvalidDataException("Ablaufschritte fehlen.");
        return new(type, title, subtitle, categories, series, metrics, columns, rows, value, maximum, unit, images, steps);
    }

    private static string Required(JsonElement n, string name, int max = 40) => Optional(n, name, max) is { Length: > 0 } value ? value : throw new InvalidDataException($"Pflichtfeld '{name}' fehlt.");
    private static string? Optional(JsonElement n, string name, int max) => n.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() is { } s && s.Length <= max ? s : throw new InvalidDataException($"'{name}' ist zu lang.") : null;
    private static double? Number(JsonElement n, string name) => n.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;
    private static List<string> Strings(JsonElement n, string name, int maxCount, int maxLength) => n.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Take(maxCount + 1).Select(v => v.ValueKind == JsonValueKind.String && (v.GetString()?.Length ?? 0) <= maxLength ? v.GetString()! : throw new InvalidDataException($"Ungültiger Wert in '{name}'.")).Take(maxCount).ToList() : [];
    private static List<VisualSeries> Series(JsonElement n) => n.TryGetProperty("series", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Take(12).Select(s => new VisualSeries(Required(s, "name", 80), s.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Take(100).Select(v => v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : double.NaN).ToList() : [], Optional(s, "color", 16))).ToList() : [];
    private static List<VisualMetric> Metrics(JsonElement n) => n.TryGetProperty("metrics", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Take(12).Select(m => new VisualMetric(Required(m, "label", 80), Number(m, "value") ?? throw new InvalidDataException("Kennzahl fehlt."), Optional(m, "unit", 24), Number(m, "change"))).ToList() : [];
    private static List<IReadOnlyList<string>> Rows(JsonElement n, int columns) => n.TryGetProperty("rows", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Take(100).Select(r => (IReadOnlyList<string>)r.EnumerateArray().Select(v => v.ToString() is { Length: <= 500 } text ? text : throw new InvalidDataException("Tabellenzelle ist zu lang.")).ToList()).Where(r => r.Count == columns).ToList() : [];
    private static List<WebImageResult> Images(JsonElement n) => n.TryGetProperty("images", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Take(12).Select(i => new WebImageResult(Required(i,"title",120), Required(i,"thumbnailUrl",2048), Required(i,"sourceUrl",2048), Optional(i,"author",120) ?? "", Optional(i,"license",80) ?? "")).Where(i => SafeImage(i.ThumbnailUrl) && SafeSource(i.SourceUrl)).ToList() : [];
    private static bool SafeImage(string value) => Uri.TryCreate(value, UriKind.Absolute, out var u) && u.Scheme == "https" && u.Host.EndsWith("wikimedia.org", StringComparison.OrdinalIgnoreCase);
    private static bool SafeSource(string value) => Uri.TryCreate(value, UriKind.Absolute, out var u) && u.Scheme == "https" && u.Host.EndsWith("wikimedia.org", StringComparison.OrdinalIgnoreCase);
    private static List<VisualStep> Steps(JsonElement n) => n.TryGetProperty("steps", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Take(30).Select(s => new VisualStep(Required(s,"label",120), Optional(s,"detail",240), Optional(s,"status",30))).ToList() : [];
}
