using System.IO;

namespace Jarvis.Services;

public static class ImageGalleryInteraction
{
    public static int ClampIndex(int index, int count) => count <= 0 ? 0 : Math.Clamp(index, 0, count - 1);
    public static int WrapIndex(int index, int count) => count <= 0 ? 0 : (index % count + count) % count;
    public static double ClampZoom(double value) => Math.Clamp(value, 1d, 8d);
    public static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string(value.Where(character => !invalid.Contains(character)).Take(80).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "image" : cleaned;
    }
}
