using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Xml.Linq;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfPoint = System.Windows.Point;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace Jarvis.Services;

/// <summary>Renders the safe SVG subset accepted by <see cref="SvgGraphicService"/> as native WPF visuals.</summary>
public static class SvgGraphicRenderer
{
    public static FrameworkElement RenderFile(string path, bool german)
    {
        try
        {
            var document = SvgGraphicService.Validate(File.ReadAllText(path));
            return Render(document, german);
        }
        catch (Exception ex)
        {
            return new Border
            {
                Padding = new Thickness(12), CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(MediaColor.FromRgb(54, 27, 34)),
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(139, 62, 73)), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = (german ? "SVG konnte nicht dargestellt werden: " : "SVG could not be rendered: ") + ex.Message, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(MediaColor.FromRgb(255, 184, 194)) }
            };
        }
    }

    public static FrameworkElement Render(XDocument document, bool german)
    {
        var root = document.Root ?? throw new InvalidDataException("SVG root is missing.");
        var viewBox = Numbers(root.Attribute("viewBox")?.Value);
        var width = viewBox.Length == 4 ? Math.Max(1, viewBox[2]) : Length(root.Attribute("width")?.Value, 800);
        var height = viewBox.Length == 4 ? Math.Max(1, viewBox[3]) : Length(root.Attribute("height")?.Value, 450);
        var left = viewBox.Length == 4 ? viewBox[0] : 0;
        var top = viewBox.Length == 4 ? viewBox[1] : 0;
        var canvas = new Canvas { Width = width, Height = height, Background = MediaBrushes.Transparent, ClipToBounds = false };
        foreach (var element in root.Elements()) AddElement(canvas, element, left, top);
        var view = new Viewbox { Child = canvas, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, MaxWidth = 740, MaxHeight = 520, HorizontalAlignment = WpfHorizontalAlignment.Stretch };
        return new Border
        {
            Child = view, Padding = new Thickness(12), Margin = new Thickness(0, 6, 0, 7),
            MinWidth = 260, MinHeight = 120, MaxWidth = 760,
            Background = new SolidColorBrush(MediaColor.FromRgb(24, 25, 32)), BorderBrush = new SolidColorBrush(MediaColor.FromRgb(59, 58, 78)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), ToolTip = german ? "Von Jarvis erzeugte SVG-Grafik" : "SVG graphic created by Jarvis"
        };
    }

    private static void AddElement(Canvas target, XElement element, double left, double top)
    {
        if (element.Name.LocalName is "defs" or "title" or "desc") return;
        if (element.Name.LocalName == "g")
        {
            var group = new Canvas { Width = target.Width, Height = target.Height };
            ApplyTransform(group, element);
            foreach (var child in element.Elements()) AddElement(group, child, left, top);
            target.Children.Add(group); return;
        }

        FrameworkElement? visual = element.Name.LocalName switch
        {
            "rect" => new WpfRectangle { Width = Length(element.Attribute("width")?.Value, 0), Height = Length(element.Attribute("height")?.Value, 0), RadiusX = Length(element.Attribute("rx")?.Value, 0), RadiusY = Length(element.Attribute("ry")?.Value, 0) },
            "circle" => Circle(element),
            "ellipse" => new Ellipse { Width = Length(element.Attribute("rx")?.Value, 0) * 2, Height = Length(element.Attribute("ry")?.Value, 0) * 2 },
            "line" => new Line { X1 = Length(element.Attribute("x1")?.Value, 0) - left, Y1 = Length(element.Attribute("y1")?.Value, 0) - top, X2 = Length(element.Attribute("x2")?.Value, 0) - left, Y2 = Length(element.Attribute("y2")?.Value, 0) - top },
            "polyline" => new Polyline { Points = Points(element.Attribute("points")?.Value, left, top) },
            "polygon" => new Polygon { Points = Points(element.Attribute("points")?.Value, left, top) },
            "path" => PathShape(element),
            "text" => TextShape(element),
            _ => null
        };
        if (visual is null) return;
        ApplyPaint(visual, element);
        ApplyTransform(visual, element);

        if (visual is WpfRectangle)
        {
            Canvas.SetLeft(visual, Length(element.Attribute("x")?.Value, 0) - left);
            Canvas.SetTop(visual, Length(element.Attribute("y")?.Value, 0) - top);
        }
        else if (visual is Ellipse)
        {
            Canvas.SetLeft(visual, Length(element.Attribute("cx")?.Value, 0) - visual.Width / 2 - left);
            Canvas.SetTop(visual, Length(element.Attribute("cy")?.Value, 0) - visual.Height / 2 - top);
        }
        else if (visual is TextBlock)
        {
            Canvas.SetLeft(visual, Length(element.Attribute("x")?.Value, 0) - left);
            Canvas.SetTop(visual, Length(element.Attribute("y")?.Value, 0) - ((TextBlock)visual).FontSize - top);
        }
        target.Children.Add(visual);
    }

    private static Ellipse Circle(XElement element)
    {
        var diameter = Length(element.Attribute("r")?.Value, 0) * 2;
        return new Ellipse { Width = diameter, Height = diameter };
    }

    private static System.Windows.Shapes.Path PathShape(XElement element)
    {
        var value = element.Attribute("d")?.Value ?? "";
        Geometry geometry;
        try { geometry = Geometry.Parse(value); }
        catch (FormatException ex) { throw new InvalidDataException("SVG path data is malformed.", ex); }
        return new System.Windows.Shapes.Path { Data = geometry };
    }

    private static TextBlock TextShape(XElement element)
    {
        var text = string.Concat(element.DescendantNodes().OfType<XText>().Select(node => node.Value));
        return new TextBlock
        {
            Text = text, FontSize = Length(element.Attribute("font-size")?.Value, 16),
            FontFamily = new WpfFontFamily(element.Attribute("font-family")?.Value ?? "Segoe UI"),
            FontWeight = (element.Attribute("font-weight")?.Value ?? "").Equals("bold", StringComparison.OrdinalIgnoreCase) ? FontWeights.Bold : FontWeights.Normal
        };
    }

    private static void ApplyPaint(FrameworkElement visual, XElement element)
    {
        var fill = Brush(element.Attribute("fill")?.Value, visual is TextBlock ? MediaBrushes.White : MediaBrushes.Transparent);
        var stroke = Brush(element.Attribute("stroke")?.Value, MediaBrushes.Transparent);
        var strokeWidth = Length(element.Attribute("stroke-width")?.Value, 1);
        var opacity = Math.Clamp(Length(element.Attribute("opacity")?.Value, 1), 0, 1);
        visual.Opacity = opacity;
        if (visual is Shape shape) { shape.Fill = fill; shape.Stroke = stroke; shape.StrokeThickness = strokeWidth; }
        if (visual is TextBlock text) text.Foreground = fill;
    }

    private static MediaBrush Brush(string? value, MediaBrush fallback)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase) || value.StartsWith("url(", StringComparison.OrdinalIgnoreCase)) return value?.Equals("none", StringComparison.OrdinalIgnoreCase) == true ? MediaBrushes.Transparent : fallback;
        try { return (MediaBrush)new BrushConverter().ConvertFromInvariantString(value)!; } catch { return fallback; }
    }

    private static PointCollection Points(string? value, double left, double top)
    {
        var values = Numbers(value);
        var points = new PointCollection();
        for (var index = 0; index + 1 < values.Length; index += 2) points.Add(new WpfPoint(values[index] - left, values[index + 1] - top));
        return points;
    }

    private static void ApplyTransform(FrameworkElement visual, XElement element)
    {
        var value = element.Attribute("transform")?.Value;
        if (string.IsNullOrWhiteSpace(value)) return;
        try { visual.RenderTransform = Transform.Parse(value.Replace(',', ' ')); }
        catch { /* Validation already prevents executable input; unsupported transforms render without transformation. */ }
    }

    private static double Length(string? value, double fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        value = value.Trim();
        foreach (var suffix in new[] { "px", "pt", "em", "%" }) if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) value = value[..^suffix.Length];
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : fallback;
    }

    private static double[] Numbers(string? value) => string.IsNullOrWhiteSpace(value)
        ? []
        : value.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => double.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN)
            .Where(double.IsFinite).ToArray();
}
