using System.IO.Pipes;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Jarvis.Services;

public enum JarvisPresenceActivity { Chatting, Voice, Coding }
public interface IDiscordRpcTransport : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken token);
    Task SendAsync(int opcode, string json, CancellationToken token);
}

public sealed class DiscordRichPresenceService : IAsyncDisposable
{
    public const string ApplicationId = "1560655725804527676";
    public const string LogoAssetKey = "jarvis-logo";
    public const string DownloadUrl = "https://my-jarvisai.netlify.app";
    private readonly Func<string, IDiscordRpcTransport> _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IDiscordRpcTransport? _transport;
    private string _applicationId = "";
    private JarvisPresenceActivity? _currentActivity;
    public DiscordRichPresenceService(Func<string, IDiscordRpcTransport>? factory = null) => _factory = factory ?? (id => new DiscordNamedPipeTransport(id));

    public async Task<bool> SetAsync(bool enabled, JarvisPresenceActivity activity, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!enabled) { await ClearCoreAsync(token); return false; }
            var applicationId = ApplicationId;
            // Always resend the complete activity, even when the broad mode did not
            // change. Discord replaces activity payloads wholesale and a prior client
            // reconnect or partial update may otherwise leave assets/buttons missing.
            try { await SetCoreAsync(applicationId, activity, token); }
            catch (IOException)
            {
                await ClearCoreAsync(CancellationToken.None);
                await SetCoreAsync(applicationId, activity, token);
            }
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task SetCoreAsync(string applicationId, JarvisPresenceActivity activity, CancellationToken token)
    {
        if (_transport is null || !string.Equals(_applicationId, applicationId, StringComparison.Ordinal))
        {
            await ClearCoreAsync(token); _applicationId = applicationId; _transport = _factory(_applicationId); await _transport.ConnectAsync(token);
            await _transport.SendAsync(0, JsonSerializer.Serialize(new { v = 1, client_id = _applicationId }), token);
        }
        var (details, state) = activity switch
        {
            JarvisPresenceActivity.Voice => ("Talking with Jarvis", "Voice session active"),
            JarvisPresenceActivity.Coding => ("Coding with Jarvis", "Working in the Jarvis workspace"),
            _ => ("Chatting with Jarvis", "Your AI. Your PC. Your Rules.")
        };
        var nonce = Guid.NewGuid().ToString("N");
        // Every mode update sends the complete activity. Discord replaces, rather than
        // merges, activities; omitting assets or buttons here would remove them.
        await _transport.SendAsync(1, JsonSerializer.Serialize(new
        {
            cmd = "SET_ACTIVITY",
            args = new
            {
                pid = Environment.ProcessId,
                activity = new
                {
                    details,
                    state,
                    assets = new { large_image = LogoAssetKey, large_text = "Jarvis" },
                    buttons = new[] { new { label = "Download Jarvis", url = DownloadUrl } }
                }
            },
            nonce
        }), token);
        _currentActivity = activity;
    }

    public async Task ClearAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try { await ClearCoreAsync(token); }
        finally { _gate.Release(); }
    }

    private async Task ClearCoreAsync(CancellationToken token)
    {
        if (_transport is not null)
        {
            try { await _transport.SendAsync(1, JsonSerializer.Serialize(new { cmd = "SET_ACTIVITY", args = new { pid = Environment.ProcessId, activity = (object?)null }, nonce = Guid.NewGuid().ToString("N") }), token); } catch { }
            await _transport.DisposeAsync(); _transport = null;
        }
        _applicationId = ""; _currentActivity = null;
    }
    public async ValueTask DisposeAsync() => await ClearAsync();
}

internal sealed class DiscordNamedPipeTransport(string applicationId) : IDiscordRpcTransport
{
    private NamedPipeClientStream? _pipe;
    public async Task ConnectAsync(CancellationToken token)
    {
        Exception? last = null;
        for (var index = 0; index < 10; index++)
        {
            try { _pipe = new NamedPipeClientStream(".", $"discord-ipc-{index}", PipeDirection.InOut, PipeOptions.Asynchronous); await _pipe.ConnectAsync(500, token); return; }
            catch (Exception ex) when (ex is TimeoutException or IOException) { last = ex; _pipe?.Dispose(); _pipe = null; }
        }
        throw new IOException($"Discord RPC is unavailable for application {applicationId}.", last);
    }
    public async Task SendAsync(int opcode, string json, CancellationToken token)
    {
        if (_pipe is null || !_pipe.IsConnected) throw new IOException("Discord RPC is not connected.");
        var payload = Encoding.UTF8.GetBytes(json); var header = new byte[8]; BitConverter.GetBytes(opcode).CopyTo(header, 0); BitConverter.GetBytes(payload.Length).CopyTo(header, 4);
        await _pipe.WriteAsync(header, token); await _pipe.WriteAsync(payload, token); await _pipe.FlushAsync(token);
        await ReadExactlyAsync(_pipe, header, token);
        var responseLength = BitConverter.ToInt32(header, 4);
        if (responseLength is < 0 or > 1_048_576) throw new IOException("Discord RPC returned an invalid frame length.");
        var response = new byte[responseLength];
        await ReadExactlyAsync(_pipe, response, token);
        using var document = JsonDocument.Parse(response);
        if (document.RootElement.TryGetProperty("evt", out var eventNode) && eventNode.ValueKind == JsonValueKind.String
            && eventNode.GetString()?.Equals("ERROR", StringComparison.OrdinalIgnoreCase) == true)
        {
            var message = document.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("message", out var messageNode)
                ? messageNode.GetString() : "Discord rejected the Rich Presence request.";
            throw new IOException(message);
        }
    }
    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), token);
            if (read == 0) throw new EndOfStreamException("Discord RPC closed the pipe.");
            offset += read;
        }
    }
    public ValueTask DisposeAsync() { _pipe?.Dispose(); _pipe = null; return ValueTask.CompletedTask; }
}
