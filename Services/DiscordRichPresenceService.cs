using System.IO.Pipes;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Jarvis.Services;

public enum JarvisPresenceActivity { Chatting, Voice, Coding }
public enum DiscordPresenceConnectionState { Disabled, Connecting, Connected, Disconnected }
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
    private readonly TimeSpan _reconnectInterval;
    private readonly CancellationTokenSource _lifetime = new();
    private IDiscordRpcTransport? _transport;
    private string _applicationId = "";
    private JarvisPresenceActivity? _currentActivity;
    private Task? _reconnectLoop;
    private bool _desiredEnabled;
    public bool IsEnabled => _desiredEnabled;
    public DiscordPresenceConnectionState ConnectionState { get; private set; } = DiscordPresenceConnectionState.Disabled;
    public event EventHandler<DiscordPresenceConnectionState>? ConnectionStateChanged;
    public DiscordRichPresenceService(Func<string, IDiscordRpcTransport>? factory = null, TimeSpan? reconnectInterval = null)
    {
        _factory = factory ?? (id => new DiscordNamedPipeTransport(id));
        _reconnectInterval = reconnectInterval ?? TimeSpan.FromSeconds(15);
    }

    public async Task<bool> SetAsync(bool enabled, JarvisPresenceActivity activity, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            _desiredEnabled = enabled;
            _currentActivity = activity;
            if (!enabled) { await DisableCoreAsync(token); return false; }
            EnsureReconnectLoop();
            return await EnsureConnectedCoreAsync(token);
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> EnsureConnectedCoreAsync(CancellationToken token)
    {
        if (!_desiredEnabled || _currentActivity is null) return false;
        SetConnectionState(DiscordPresenceConnectionState.Connecting);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        operation.CancelAfter(_transport is null ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(4));
        var ioToken = operation.Token;
        try
        {
            if (_transport is null || !string.Equals(_applicationId, ApplicationId, StringComparison.Ordinal))
            {
                await DropTransportCoreAsync();
                _applicationId = ApplicationId;
                _transport = _factory(_applicationId);
                await _transport.ConnectAsync(ioToken);
                await _transport.SendAsync(0, JsonSerializer.Serialize(new { v = 1, client_id = _applicationId }), ioToken);
            }
            await SendActivityCoreAsync(_currentActivity.Value, ioToken);
            SetConnectionState(DiscordPresenceConnectionState.Connected);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            await DropTransportCoreAsync();
            SetConnectionState(DiscordPresenceConnectionState.Disconnected);
            return false;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            await DropTransportCoreAsync();
            SetConnectionState(DiscordPresenceConnectionState.Disconnected);
            return false;
        }
    }

    private async Task SendActivityCoreAsync(JarvisPresenceActivity activity, CancellationToken token)
    {
        if (_transport is null) throw new IOException("Discord RPC is not connected.");
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

    private void EnsureReconnectLoop()
    {
        if (_reconnectLoop is { IsCompleted: false }) return;
        _reconnectLoop = Task.Run(ReconnectLoopAsync);
    }

    private async Task ReconnectLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try { await Task.Delay(_reconnectInterval, _lifetime.Token); }
            catch (OperationCanceledException) { return; }
            if (!_desiredEnabled) continue;
            try
            {
                await _gate.WaitAsync(_lifetime.Token);
                try
                {
                    // A complete SET_ACTIVITY also acts as a bounded liveness probe.
                    // On failure the transport is dropped and the next tick reconnects.
                    await EnsureConnectedCoreAsync(_lifetime.Token);
                }
                finally { _gate.Release(); }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch { SetConnectionState(DiscordPresenceConnectionState.Disconnected); }
        }
    }

    public async Task ClearAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try { _desiredEnabled = false; await DisableCoreAsync(token); }
        finally { _gate.Release(); }
    }

    private async Task DisableCoreAsync(CancellationToken token)
    {
        if (_transport is not null)
        {
            try { await _transport.SendAsync(1, JsonSerializer.Serialize(new { cmd = "SET_ACTIVITY", args = new { pid = Environment.ProcessId, activity = (object?)null }, nonce = Guid.NewGuid().ToString("N") }), token); } catch { }
        }
        await DropTransportCoreAsync();
        _applicationId = ""; _currentActivity = null; SetConnectionState(DiscordPresenceConnectionState.Disabled);
    }

    private async Task DropTransportCoreAsync()
    {
        if (_transport is null) return;
        try { await _transport.DisposeAsync(); } catch { }
        _transport = null;
        _applicationId = "";
    }

    private void SetConnectionState(DiscordPresenceConnectionState state)
    {
        if (ConnectionState == state) return;
        ConnectionState = state;
        ConnectionStateChanged?.Invoke(this, state);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await ClearAsync();
        if (_reconnectLoop is not null) try { await _reconnectLoop; } catch { }
        _lifetime.Dispose(); _gate.Dispose();
    }
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
