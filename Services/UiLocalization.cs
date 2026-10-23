using System.Diagnostics;
using System.Globalization;
using System.Resources;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Jarvis.Services;

/// <summary>Centralized, runtime-switchable first-party UI localization.</summary>
public static class UiLocalization
{
    private static readonly ResourceManager Resources = new("Jarvis.Localization.UiText", typeof(UiLocalization).Assembly);
    private static readonly object IndexLock = new();
    private static Dictionary<string, string>? _keyByText;
    private static readonly HashSet<string> Missing = new(StringComparer.Ordinal);

    public static string DetectInitialLanguage(CultureInfo? windowsCulture = null)
    {
        // A missing preference is a first-run state and now defaults to English.
        // Explicit preferences are loaded before this method is called and remain unchanged.
        return "en";
    }

    public static string Translate(string value, string language)
    {
        if (string.IsNullOrEmpty(value) || value == "Tools") return value;
        EnsureIndex();
        var culture = CultureFor(language);
        if (_keyByText!.TryGetValue(value, out var key))
            return Resources.GetString(key, culture) ?? MissingFallback(value, key);

        // Preserve variable suffixes in localized status/error prefixes.
        foreach (var candidate in _keyByText.Keys.Where(k => (k.EndsWith(':') || k.EndsWith(" at", StringComparison.Ordinal) || k.EndsWith(" ·", StringComparison.Ordinal)) && value.StartsWith(k, StringComparison.Ordinal)).OrderByDescending(k => k.Length))
        {
            var prefixKey = _keyByText[candidate];
            var sourcePrefix = Resources.GetString(prefixKey, culture);
            if (sourcePrefix is not null) return sourcePrefix + value[candidate.Length..];
        }
        // Never log dynamic UI values: they may contain personal data.
        lock (Missing) { Missing.Add(value); }
        return value;
    }

    public static void Apply(DependencyObject root, string language, DependencyObject? excludeSubtree = null)
    {
        // Translating TextBlock.Text can replace its inline tree. Snapshot before mutation.
        foreach (var node in new[] { root }.Concat(Descendants(root)).ToArray())
        {
            if (excludeSubtree is not null && (ReferenceEquals(node, excludeSubtree) || IsDescendantOf(node, excludeSubtree))) continue;
            if (node is FrameworkElement tip && tip.ToolTip is string tipText)
                tip.ToolTip = Translate(tipText, language);
            if (node is DataGrid grid)
                foreach (var column in grid.Columns)
                    if (column.Header is string columnHeader) column.Header = Translate(columnHeader, language);
            switch (node)
            {
                case TextBlock text when !string.IsNullOrWhiteSpace(text.Text) && !BindingOperations.IsDataBound(text, TextBlock.TextProperty):
                    text.Text = Translate(text.Text, language); break;
                case System.Windows.Controls.Button button when button.Content is string buttonContent && !BindingOperations.IsDataBound(button, ContentControl.ContentProperty):
                    button.Content = Translate(buttonContent, language); break;
                case System.Windows.Controls.CheckBox check when check.Content is string checkContent && !BindingOperations.IsDataBound(check, ContentControl.ContentProperty):
                    check.Content = Translate(checkContent, language); break;
                case ComboBoxItem item when item.Content is string itemContent:
                    item.Content = Translate(itemContent, language); break;
                case ContentControl control when control.Content is string controlContent && !BindingOperations.IsDataBound(control, ContentControl.ContentProperty):
                    control.Content = Translate(controlContent, language); break;
                case TabItem tab when tab.Header is string header:
                    tab.Header = Translate(header, language); break;
                case Expander expander when expander.Header is string heading:
                    expander.Header = Translate(heading, language); break;
                case FrameworkElement element when element.ToolTip is string tooltip:
                    element.ToolTip = Translate(tooltip, language); break;

            }
        }
    }

    private static CultureInfo CultureFor(string language) => language.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? CultureInfo.GetCultureInfo("de-DE") : CultureInfo.GetCultureInfo("en-US");

    private static string MissingFallback(string value, string key)
    {
        lock (Missing)
            if (Missing.Add(key)) Trace.TraceWarning("UI localization resource '{0}' has no value for the requested culture", key);
        return value;
    }

    private static void EnsureIndex()
    {
        if (_keyByText is not null) return;
        lock (IndexLock)
        {
            if (_keyByText is not null) return;
            var index = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var culture in new[] { CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("de-DE") })
            {
                var set = Resources.GetResourceSet(culture, true, true);
                if (set is null) continue;
                foreach (System.Collections.DictionaryEntry item in set)
                    if (item.Key is string key && item.Value is string text && !index.ContainsKey(text)) index[text] = key;
            }
            _keyByText = index;
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        return DescendantsCore(root, new HashSet<DependencyObject> { root });
    }

    private static IEnumerable<DependencyObject> DescendantsCore(DependencyObject root, HashSet<DependencyObject> visited)
    {
        var children = new List<DependencyObject>();
        foreach (var logical in LogicalTreeHelper.GetChildren(root))
            if (logical is DependencyObject child && visited.Add(child)) children.Add(child);

        if (root is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var visual = VisualTreeHelper.GetChild(root, index);
                if (visited.Add(visual)) children.Add(visual);
            }
        }

        foreach (var child in children)
        {
            yield return child;
            foreach (var nested in DescendantsCore(child, visited)) yield return nested;
        }
    }

    private static DependencyObject? ParentOf(DependencyObject node) => node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private static bool IsDescendantOf(DependencyObject node, DependencyObject parent)
    {
        for (var current = ParentOf(node); current is not null; current = ParentOf(current))
            if (ReferenceEquals(current, parent)) return true;
        return false;
    }
}
