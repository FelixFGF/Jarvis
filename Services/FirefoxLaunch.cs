namespace Jarvis.Services;

public static class FirefoxLaunch
{
    public static bool IsMozillaFirefoxProduct(string? productName, string? companyName) =>
        string.Equals(productName?.Trim(), "Firefox", StringComparison.OrdinalIgnoreCase)
        && string.Equals(companyName?.Trim(), "Mozilla Corporation", StringComparison.OrdinalIgnoreCase);

    public static string[] BuildPrivateWindowArguments(string? url)
    {
        var normalized = string.IsNullOrWhiteSpace(url) ? null : ValidateHttpUrl(url);
        return normalized is null ? ["-private-window"] : ["-private-window", normalized];
    }

    public static string ValidateHttpUrl(string value)
    {
        if (value.Length > 2048
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.UserInfo.Length > 0)
            throw new ArgumentException("Only a valid HTTP or HTTPS address without embedded credentials can be opened in Firefox.", nameof(value));
        return uri.AbsoluteUri;
    }
}
