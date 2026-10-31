using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Jarvis.Services;

public sealed record SvgGraphicRequest(string Name, string SvgContent, string Action, bool DisplayInChat);
public sealed record SvgGraphicResult(string Name, string Path, string Action, bool DisplayInChat, long Bytes, int Version);

/// <summary>Validates and persists model-generated SVGs within Jarvis's private data directory.</summary>
public sealed class SvgGraphicService
{
    public const int MaximumBytes = 512 * 1024;
    public const int MaximumElements = 2_500;

    private static readonly HashSet<string> AllowedElements = new(StringComparer.Ordinal)
    {
        "svg", "g", "defs", "title", "desc", "rect", "circle", "ellipse", "line", "polyline", "polygon",
        "path", "text", "tspan", "linearGradient", "radialGradient", "stop", "clipPath"
    };
    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.Ordinal)
    {
        "id", "viewBox", "width", "height", "x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "rx", "ry",
        "d", "points", "fill", "fill-opacity", "stroke", "stroke-width", "stroke-opacity", "stroke-linecap", "stroke-linejoin",
        "opacity", "transform", "font-family", "font-size", "font-weight", "text-anchor", "dominant-baseline", "offset",
        "stop-color", "stop-opacity", "gradientUnits", "gradientTransform", "fx", "fy", "clip-path"
    };
    private static readonly Regex SafeName = new("[^a-zA-Z0-9._-]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LocalReference = new(@"^url\(#[A-Za-z_][A-Za-z0-9_.:-]*\)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly string _root;

    public SvgGraphicService(string? root = null)
    {
        _root = Path.GetFullPath(root ?? JarvisStoragePaths.CreateDefault().SvgRoot);
    }

    public string Root => _root;

    public async Task<SvgGraphicResult> ExecuteAsync(SvgGraphicRequest request, CancellationToken cancellationToken = default)
    {
        var action = (request.Action ?? "create").Trim().ToLowerInvariant();
        if (action is not ("create" or "update" or "reuse")) throw new InvalidDataException("SVG action must be create, update, or reuse.");
        var safeName = NormalizeName(request.Name);
        Directory.CreateDirectory(_root);
        var path = ResolveSafePath(safeName);

        if (action == "reuse")
        {
            if (!File.Exists(path)) throw new FileNotFoundException("The requested SVG does not exist.", path);
            var existing = await File.ReadAllTextAsync(path, cancellationToken);
            ValidateAndNormalize(existing);
            return new(safeName, path, action, request.DisplayInChat, new FileInfo(path).Length, ReadVersion(safeName));
        }

        var normalized = ValidateAndNormalize(request.SvgContent);
        if (action == "create" && File.Exists(path))
        {
            var stem = Path.GetFileNameWithoutExtension(safeName);
            var version = 2;
            do { safeName = $"{stem}-v{version++}.svg"; path = ResolveSafePath(safeName); } while (File.Exists(path));
        }
        else if (action == "update" && !File.Exists(path))
        {
            throw new FileNotFoundException("The SVG to update does not exist.", path);
        }

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, normalized, new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new(safeName, path, action, request.DisplayInChat, new FileInfo(path).Length, ReadVersion(safeName));
    }

    public static XDocument Validate(string svg) => XDocument.Parse(ValidateAndNormalize(svg), LoadOptions.PreserveWhitespace);

    private static string ValidateAndNormalize(string? svg)
    {
        if (string.IsNullOrWhiteSpace(svg)) throw new InvalidDataException("SVG content is empty.");
        if (Encoding.UTF8.GetByteCount(svg) > MaximumBytes) throw new InvalidDataException("SVG content exceeds the 512 KB limit.");
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(svg), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes });
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex) { throw new InvalidDataException("SVG XML is malformed.", ex); }
        var root = document.Root;
        if (root is null || root.Name.LocalName != "svg" || root.Name.NamespaceName is not ("" or "http://www.w3.org/2000/svg"))
            throw new InvalidDataException("The document root must be an SVG element.");
        var elements = root.DescendantsAndSelf().ToArray();
        if (elements.Length > MaximumElements) throw new InvalidDataException("SVG complexity exceeds the element limit.");
        foreach (var element in elements)
        {
            if (!AllowedElements.Contains(element.Name.LocalName)) throw new InvalidDataException($"SVG element '{element.Name.LocalName}' is not allowed.");
            if (element.Name.NamespaceName is not ("" or "http://www.w3.org/2000/svg")) throw new InvalidDataException("Foreign XML namespaces are not allowed.");
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration) continue;
                var name = attribute.Name.LocalName;
                if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) || !AllowedAttributes.Contains(name))
                    throw new InvalidDataException($"SVG attribute '{name}' is not allowed.");
                var value = attribute.Value.Trim();
                if (value.Contains("javascript:", StringComparison.OrdinalIgnoreCase) || value.Contains("data:", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("file:", StringComparison.OrdinalIgnoreCase) || value.Contains("http:", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("https:", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("External or executable SVG references are not allowed.");
                if (value.Contains("url(", StringComparison.OrdinalIgnoreCase) && !LocalReference.IsMatch(value))
                    throw new InvalidDataException("Only local SVG fragment references are allowed.");
            }
        }
        return document.ToString(SaveOptions.DisableFormatting);
    }

    private string ResolveSafePath(string name)
    {
        var path = Path.GetFullPath(Path.Combine(_root, name));
        var prefix = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SVG path escapes the Jarvis SVG directory.");
        return path;
    }

    private static string NormalizeName(string? name)
    {
        name = (name ?? "").Trim();
        if (!name.Equals(Path.GetFileName(name), StringComparison.Ordinal) || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            throw new InvalidDataException("SVG filenames cannot contain a directory path.");
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") throw new InvalidDataException("A descriptive SVG filename is required.");
        name = SafeName.Replace(name, "-").Trim('-', '.');
        if (name.Length is 0 or > 96) throw new InvalidDataException("SVG filename is invalid or too long.");
        if (!name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) name += ".svg";
        return name;
    }

    private static int ReadVersion(string name)
    {
        var match = Regex.Match(Path.GetFileNameWithoutExtension(name), @"-v(?<number>\d+)$", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 1;
    }
}
