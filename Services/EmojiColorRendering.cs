using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Jarvis.Services;

public static class EmojiColorRendering
{
    public const string VerificationText = "😀 😎 🚀 🎮 🧠 ❤️ 🔥 ✅ ⚙️ 🌍 👨‍💻 👩‍🚀 🏳️‍🌈";

    public static Emoji.Wpf.RichTextBox CreateRichTextBox(FlowDocument document)
    {
        Emoji.Wpf.EmojiData.EnableSubPixelRendering = true;
        return new Emoji.Wpf.RichTextBox { Document = document };
    }

    public static (byte[] Png, int ColoredPixels) RenderVerificationImage()
    {
        var paragraph = new Paragraph(new Run(VerificationText)) { Margin = new Thickness(8), FontSize = 34, Foreground = System.Windows.Media.Brushes.White };
        var control = CreateRichTextBox(new FlowDocument(paragraph) { PagePadding = new Thickness(0) });
        control.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 20, 26));
        control.BorderThickness = new Thickness(0);
        control.IsReadOnly = true;
        control.Width = 820; control.Height = 86;
        control.Measure(new System.Windows.Size(820, 86)); control.Arrange(new Rect(0, 0, 820, 86)); control.UpdateLayout();
        var bitmap = new RenderTargetBitmap(820, 86, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(control);
        var pixels = new byte[820 * 86 * 4];
        bitmap.CopyPixels(pixels, 820 * 4, 0);
        var colored = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var b = pixels[i]; var g = pixels[i + 1]; var r = pixels[i + 2]; var a = pixels[i + 3];
            if (a > 40 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 24) colored++;
        }
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream);
        return (stream.ToArray(), colored);
    }
}
