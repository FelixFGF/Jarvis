using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Jarvis.Services;

public sealed record AudioSessionState(
    string SessionId,
    int ProcessId,
    string ProcessName,
    string DisplayName,
    bool IsActive,
    int VolumePercent,
    bool IsMuted,
    float PeakValue);

public sealed record SystemAudioState(int VolumePercent, bool IsMuted);
public sealed record AudioMutationResult(bool Ok, string Error, IReadOnlyList<AudioSessionState> Sessions, SystemAudioState? System,
    IReadOnlyList<string>? Candidates = null);

public interface IWindowsAudioBackend
{
    SystemAudioState GetSystemState();
    void SetSystemVolume(float scalar);
    void SetSystemMute(bool muted);
    IReadOnlyList<AudioSessionState> ListSessions();
    int SetSessionVolume(IReadOnlyCollection<string> sessionIds, float scalar);
    int SetSessionMute(IReadOnlyCollection<string> sessionIds, bool muted);
}

public sealed class WindowsAudioControlService(IWindowsAudioBackend? backend = null)
{
    private readonly IWindowsAudioBackend _backend = backend ?? new CoreAudioBackend();
    private readonly Dictionary<string, IReadOnlyList<AudioSessionState>> _previousStates = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<AudioSessionState> ListSessions() => _backend.ListSessions()
        .OrderByDescending(session => session.IsActive)
        .ThenBy(session => session.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    public SystemAudioState GetSystemState() => _backend.GetSystemState();

    public AudioMutationResult SetSystemVolume(int percent)
    {
        ValidatePercent(percent);
        _backend.SetSystemVolume(percent / 100f);
        return new(true, "", [], _backend.GetSystemState());
    }

    public AudioMutationResult SetSystemMute(bool muted)
    {
        _backend.SetSystemMute(muted);
        return new(true, "", [], _backend.GetSystemState());
    }

    public AudioMutationResult SetApplicationVolume(string application, int percent) =>
        ChangeApplication(application, sessions => _backend.SetSessionVolume(sessions, percent / 100f), percent);

    public AudioMutationResult SetApplicationMute(string application, bool muted) =>
        ChangeApplication(application, sessions => _backend.SetSessionMute(sessions, muted), null);

    public AudioMutationResult RestoreApplicationVolume(string application)
    {
        var key = Normalize(application);
        if (!_previousStates.TryGetValue(key, out var previous) || previous.Count == 0)
            return new(false, $"No previous audio state is stored for '{application}'.", [], null);
        var changed = 0;
        foreach (var group in previous.GroupBy(item => item.VolumePercent))
            changed += _backend.SetSessionVolume(group.Select(item => item.SessionId).ToArray(), group.Key / 100f);
        foreach (var group in previous.GroupBy(item => item.IsMuted))
            changed += _backend.SetSessionMute(group.Select(item => item.SessionId).ToArray(), group.Key);
        if (changed == 0) return new(false, $"The previous audio sessions for '{application}' are no longer available.", [], null);
        var ids = previous.Select(item => item.SessionId).ToHashSet(StringComparer.Ordinal);
        var updated = _backend.ListSessions().Where(item => ids.Contains(item.SessionId)).ToArray();
        _previousStates.Remove(key);
        return new(true, "", updated, null);
    }

    public AudioMutationResult SetAllApplicationVolumes(int percent)
    {
        ValidatePercent(percent);
        var current = _backend.ListSessions();
        if (current.Count == 0) return new(false, "No Windows audio sessions are currently available.", [], null);
        var ids = current.Select(item => item.SessionId).Distinct(StringComparer.Ordinal).ToArray();
        _backend.SetSessionVolume(ids, percent / 100f);
        var updated = _backend.ListSessions();
        return new(true, "", updated, null);
    }

    private AudioMutationResult ChangeApplication(string application, Func<IReadOnlyCollection<string>, int> mutate, int? expectedPercent)
    {
        var query = Normalize(application);
        if (query.Length == 0) return new(false, "An application name is required.", [], null);
        var current = _backend.ListSessions();
        var matches = current.Where(session => Matches(session, query)).ToArray();
        if (matches.Length == 0)
            return new(false, $"No Windows audio session matched '{application}'. The application may be closed or not have created an audio session yet.", [], null);
        var identities = matches.Select(item => Normalize(item.ProcessName)).Where(item => item.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (identities.Length > 1)
            return new(false, $"'{application}' matches multiple running audio applications. Choose one exact application.", [], null,
                matches.Select(item => item.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray());
        _previousStates[query] = matches.Select(item => item with { }).ToArray();
        var ids = matches.Select(item => item.SessionId).Distinct(StringComparer.Ordinal).ToArray();
        var changed = mutate(ids);
        if (changed == 0) return new(false, $"The matching audio sessions for '{application}' could not be changed.", [], null);
        var updated = _backend.ListSessions().Where(session => ids.Contains(session.SessionId, StringComparer.Ordinal)).ToArray();
        if (updated.Length == 0 || (expectedPercent.HasValue && updated.Any(item => Math.Abs(item.VolumePercent - expectedPercent.Value) > 1)))
            return new(false, "Windows did not verify the requested audio state after the change.", updated, null);
        return new(true, "", updated, null);
    }

    private static bool Matches(AudioSessionState session, string query) =>
        Normalize(session.ProcessName).Contains(query, StringComparison.Ordinal)
        || Normalize(session.DisplayName).Contains(query, StringComparison.Ordinal)
        || query.Contains(Normalize(session.ProcessName), StringComparison.Ordinal);

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static void ValidatePercent(int percent)
    {
        if (percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent), "Volume must be between 0 and 100 percent.");
    }

    private sealed class CoreAudioBackend : IWindowsAudioBackend
    {
        public SystemAudioState GetSystemState()
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return new((int)Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100), device.AudioEndpointVolume.Mute);
        }

        public void SetSystemVolume(float scalar)
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(scalar, 0f, 1f);
        }

        public void SetSystemMute(bool muted)
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            device.AudioEndpointVolume.Mute = muted;
        }

        public IReadOnlyList<AudioSessionState> ListSessions()
        {
            var result = new List<AudioSessionState>();
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            using (device)
            {
                var sessions = device.AudioSessionManager?.Sessions;
                if (sessions is null) continue;
                for (var index = 0; index < sessions.Count; index++)
                {
                    using var session = sessions[index];
                    try
                    {
                        using var volume = session.SimpleAudioVolume;
                        var processId = SafeProcessId(session);
                        var processName = SafeProcessName(processId);
                        var display = string.IsNullOrWhiteSpace(session.DisplayName) ? processName : session.DisplayName.Trim();
                        var sessionId = $"{device.ID}|{session.GetSessionIdentifier}|{processId}";
                        var peak = 0f;
                        try { peak = session.AudioMeterInformation.MasterPeakValue; } catch { }
                        result.Add(new(sessionId, processId, processName, display,
                            string.Equals(session.State.ToString(), "Active", StringComparison.OrdinalIgnoreCase),
                            (int)Math.Round(volume.Volume * 100), volume.Mute, peak));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or COMException) { }
                }
            }
            return result;
        }

        public int SetSessionVolume(IReadOnlyCollection<string> sessionIds, float scalar) => ChangeSessions(sessionIds, volume => volume.Volume = Math.Clamp(scalar, 0f, 1f));
        public int SetSessionMute(IReadOnlyCollection<string> sessionIds, bool muted) => ChangeSessions(sessionIds, volume => volume.Mute = muted);

        private static int ChangeSessions(IReadOnlyCollection<string> sessionIds, Action<SimpleAudioVolume> mutation)
        {
            var changed = 0;
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            using (device)
            {
                var sessions = device.AudioSessionManager?.Sessions;
                if (sessions is null) continue;
                for (var index = 0; index < sessions.Count; index++)
                {
                    using var session = sessions[index];
                    try
                    {
                        var id = $"{device.ID}|{session.GetSessionIdentifier}|{SafeProcessId(session)}";
                        if (!sessionIds.Contains(id)) continue;
                        using var volume = session.SimpleAudioVolume;
                        mutation(volume); changed++;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or COMException) { }
                }
            }
            return changed;
        }

        private static int SafeProcessId(AudioSessionControl session) { try { return checked((int)session.GetProcessID); } catch { return 0; } }
        private static string SafeProcessName(int processId)
        {
            if (processId <= 0) return "System Sounds";
            try { using var process = Process.GetProcessById(processId); return process.ProcessName; }
            catch { return $"Process {processId}"; }
        }
    }
}
