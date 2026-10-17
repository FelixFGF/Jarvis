using System.Text.RegularExpressions;

namespace Jarvis.Services;

/// <summary>Recognizes explicit Spotify volume instructions before model tool calling.</summary>
public static class SpotifyCommandParser
{
    private static readonly Regex SpotifyMention = new(@"\bspotify\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VolumeIntent = new(@"\b(lautstärke|volume|lauter|leiser|max(?:imum)?|maximal(?:e|en|er)?|voll(?:e|en|er)?|prozent|percent|stumm|mute|min(?:imum)?|minimal|null|zero)\b|(?<!\d)\d{1,3}\s*%", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DownIntent = new(@"\b(leiser|lower|reduce|reduzier|senk|decrease)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UpIntent = new(@"\b(lauter|louder|increase|erhöh|steiger)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MuteIntent = new(@"\b(stumm|mute|minimum|minimal|null|zero)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MaximumIntent = new(@"\b(max(?:imum)?|maximal(?:e|en|er)?|voll(?:e|en|er)?|100)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Number = new(@"(?<!\d)(\d{1,3})\s*%?", RegexOptions.Compiled);

    public static bool TryParse(string text, out string action, out int percent)
    {
        action = "";
        percent = 0;
        if (!SpotifyMention.IsMatch(text) || !VolumeIntent.IsMatch(text)) return false;
        if (DownIntent.IsMatch(text))
        {
            action = "volume_down";
            percent = ParsePercent(text, 5);
            return true;
        }
        if (UpIntent.IsMatch(text))
        {
            action = "volume_up";
            percent = ParsePercent(text, 5);
            return true;
        }
        action = "set_volume";
        percent = MuteIntent.IsMatch(text) ? 0 : MaximumIntent.IsMatch(text) ? 100 : ParsePercent(text, -1);
        return percent >= 0;
    }

    private static int ParsePercent(string text, int fallback) =>
        int.TryParse(Number.Match(text).Groups[1].Value, out var value) && value <= 100 ? value : fallback;
}
