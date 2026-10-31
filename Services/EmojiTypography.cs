using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfFontStyle = System.Windows.FontStyle;

namespace Jarvis.Services;

public static class EmojiTypography
{
    public static readonly WpfFontFamily EmojiFont = new("Segoe UI Emoji");

    public static void Append(InlineCollection target, string text, FontWeight? weight = null, WpfFontStyle? style = null)
    {
        var ordinaryText = new StringBuilder();
        void FlushOrdinaryText()
        {
            if (ordinaryText.Length == 0) return;
            target.Add(CreateRun(ordinaryText.ToString(), null, weight, style));
            ordinaryText.Clear();
        }
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (!IsEmojiElement(element)) { ordinaryText.Append(element); continue; }
            FlushOrdinaryText();
            target.Add(CreateRun(element, EmojiFont, weight, style));
        }
        FlushOrdinaryText();
    }

    private static Run CreateRun(string text, WpfFontFamily? font, FontWeight? weight, WpfFontStyle? style)
    {
        var run = new Run(text);
        if (font is not null)
        {
            run.FontFamily = font;
            TextOptions.SetTextFormattingMode(run, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(run, TextRenderingMode.ClearType);
            TextOptions.SetTextHintingMode(run, TextHintingMode.Fixed);
        }
        if (weight is { } fontWeight) run.FontWeight = fontWeight;
        if (style is { } fontStyle) run.FontStyle = fontStyle;
        return run;
    }

    public static bool IsEmojiElement(string element)
    {
        foreach (var rune in element.EnumerateRunes())
        {
            var value = rune.Value;
            if (value is >= 0x1F000 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF or >= 0x1F1E6 and <= 0x1F1FF
                || value is 0xFE0F or 0x200D or 0x20E3) return true;
        }
        return false;
    }
}
