using System.Text.Json;

namespace Jarvis.Beta;

public sealed record SupabaseOptions(string Url, string PublishableKey)
{
    public bool IsConfigured => Uri.TryCreate(Url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        && IsPublicClientKey(PublishableKey);

    private static bool IsPublicClientKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.StartsWith("sb_secret_", StringComparison.Ordinal)) return false;
        // Supabase supports the new publishable-key format and legacy anon JWT keys.
        return key.StartsWith("sb_publishable_", StringComparison.Ordinal)
            || (key.StartsWith("eyJ", StringComparison.Ordinal) && key.Split('.').Length == 3);
    }

    public static SupabaseOptions Load(string filePath, string? defaultUrl = null, string? defaultKey = null)
    {
        var fromFile = File.Exists(filePath)
            ? JsonSerializer.Deserialize<SupabaseOptions>(File.ReadAllText(filePath), JsonOptions) ?? new("", "")
            : new(defaultUrl ?? "", defaultKey ?? "");
        var environmentUrl = Environment.GetEnvironmentVariable("JARVIS_SUPABASE_URL");
        var environmentKey = Environment.GetEnvironmentVariable("JARVIS_SUPABASE_PUBLISHABLE_KEY");
        return new(
            string.IsNullOrWhiteSpace(environmentUrl) ? (string.IsNullOrWhiteSpace(fromFile.Url) ? defaultUrl : fromFile.Url) ?? "" : environmentUrl,
            string.IsNullOrWhiteSpace(environmentKey) ? (string.IsNullOrWhiteSpace(fromFile.PublishableKey) ? defaultKey : fromFile.PublishableKey) ?? "" : environmentKey);
    }

    public void Save(string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}
