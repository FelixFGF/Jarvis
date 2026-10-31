using System.Net;
using System.Net.Sockets;
using System.IO;

namespace Jarvis.Services;

public enum ProviderTimeoutKind { RequestDeadline, StreamingInactivity, ToolExecution }

public sealed class ProviderTimeoutException(ProviderTimeoutKind kind, string providerLabel, TimeSpan limit, Exception? inner = null)
    : TimeoutException($"{kind} timeout for {providerLabel} after {limit.TotalSeconds:0} seconds.", inner)
{
    public ProviderTimeoutKind Kind { get; } = kind;
    public string ProviderLabel { get; } = providerLabel;
    public TimeSpan Limit { get; } = limit;
}

public sealed record ProviderTimeoutSettings(
    int LocalRequestSeconds = 900,
    int LocalStreamingInactivitySeconds = 180,
    int CloudRequestSeconds = 180,
    int CloudStreamingInactivitySeconds = 90,
    int MaximumTaskSeconds = 1800,
    int DefaultToolSeconds = 300)
{
    public ProviderTimeoutSettings Validate() => this with
    {
        LocalRequestSeconds = Math.Clamp(LocalRequestSeconds, 60, 3600),
        LocalStreamingInactivitySeconds = Math.Clamp(LocalStreamingInactivitySeconds, 30, 900),
        CloudRequestSeconds = Math.Clamp(CloudRequestSeconds, 30, 900),
        CloudStreamingInactivitySeconds = Math.Clamp(CloudStreamingInactivitySeconds, 15, 300),
        MaximumTaskSeconds = Math.Clamp(MaximumTaskSeconds, 300, 7200),
        DefaultToolSeconds = Math.Clamp(DefaultToolSeconds, 30, 1800)
    };
}

public readonly record struct ProviderTimeoutBudget(bool IsLocal, TimeSpan RequestDeadline, TimeSpan StreamingInactivity, TimeSpan MaximumTask, TimeSpan DefaultTool);

public static class ProviderTimeoutPolicy
{
    public static async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken userToken,
        ProviderTimeoutKind kind, string providerLabel, TimeSpan limit)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(userToken);
        deadline.CancelAfter(limit);
        try { return await operation(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (!userToken.IsCancellationRequested)
        {
            throw new ProviderTimeoutException(kind, providerLabel, limit, ex);
        }
    }

    public static Task<string?> ReadLineAsync(TextReader reader, string providerLabel, TimeSpan inactivityLimit, CancellationToken userToken) =>
        ExecuteAsync(token => reader.ReadLineAsync(token).AsTask(), userToken, ProviderTimeoutKind.StreamingInactivity, providerLabel, inactivityLimit);

    public static ProviderTimeoutBudget Resolve(string endpoint, ProviderTimeoutSettings settings)
    {
        settings = settings.Validate();
        var local = IsLocalEndpoint(endpoint);
        return new ProviderTimeoutBudget(
            local,
            TimeSpan.FromSeconds(local ? settings.LocalRequestSeconds : settings.CloudRequestSeconds),
            TimeSpan.FromSeconds(local ? settings.LocalStreamingInactivitySeconds : settings.CloudStreamingInactivitySeconds),
            TimeSpan.FromSeconds(settings.MaximumTaskSeconds),
            TimeSpan.FromSeconds(settings.DefaultToolSeconds));
    }

    public static bool IsLocalEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(uri.Host, out var address)) return false;
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily != AddressFamily.InterNetwork) return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || bytes[0] == 127
            || bytes[0] == 192 && bytes[1] == 168
            || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            || bytes[0] == 169 && bytes[1] == 254;
    }

    public static string UserMessage(ProviderTimeoutException exception, bool german) => (exception.Kind, german) switch
    {
        (ProviderTimeoutKind.StreamingInactivity, true) => $"Der Anbieter hat {exception.Limit.TotalSeconds:0} Sekunden lang keine neuen Streaming-Daten gesendet. Die Verbindung wurde als festgefahren beendet; bereits empfangene Ergebnisse bleiben erhalten.",
        (ProviderTimeoutKind.StreamingInactivity, false) => $"The provider sent no new streaming data for {exception.Limit.TotalSeconds:0} seconds. The stalled connection was stopped; results already received were preserved.",
        (ProviderTimeoutKind.ToolExecution, true) => $"Ein Werkzeug hat sein Zeitlimit von {exception.Limit.TotalSeconds:0} Sekunden erreicht. Es wurde nicht automatisch erneut ausgeführt.",
        (ProviderTimeoutKind.ToolExecution, false) => $"A tool reached its {exception.Limit.TotalSeconds:0}-second limit. It was not executed again automatically.",
        (_, true) => $"Der KI-Anbieter hat das Zeitlimit von {exception.Limit.TotalSeconds:0} Sekunden erreicht. Die Anfrage wurde nicht vom Nutzer abgebrochen und es wurden keine Aktionen wiederholt.",
        _ => $"The AI provider reached its {exception.Limit.TotalSeconds:0}-second limit. The request was not cancelled by the user and no actions were repeated."
    };
}
