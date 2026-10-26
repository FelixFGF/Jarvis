using System.Text.Json;
using System.IO;
using NAudio.Wave;

namespace Jarvis.Services;

public enum ReminderRecurrence { None, Daily, Weekly }

public sealed record ReminderRecord(string Title, DateTimeOffset DueAt, string State, bool Notified, DateTimeOffset CreatedAt)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public ReminderRecurrence Recurrence { get; init; }
    public DateTimeOffset? LastFiredAt { get; init; }
    public DateTimeOffset? SnoozedFrom { get; init; }
    public string Description { get; init; } = "";
}

public static class ReminderRules
{
    public static ReminderRecord Create(string title, DateTimeOffset dueAt, ReminderRecurrence recurrence, DateTimeOffset now)
    {
        title = (title ?? "").Trim();
        if (title.Length is 0 or > 180) throw new ArgumentException("Reminder title must contain 1–180 characters.", nameof(title));
        if (dueAt <= now) throw new ArgumentOutOfRangeException(nameof(dueAt), "Reminder time must be in the future.");
        return new(title, dueAt, "Pending", false, now) { Recurrence = recurrence };
    }

    public static bool IsDuplicate(IEnumerable<ReminderRecord> reminders, ReminderRecord candidate) => reminders.Any(existing =>
        existing.State is not ("Cancelled" or "Dismissed") &&
        existing.Title.Equals(candidate.Title, StringComparison.CurrentCultureIgnoreCase) &&
        Math.Abs((existing.DueAt - candidate.DueAt).TotalSeconds) < 30 && existing.Recurrence == candidate.Recurrence);

    public static IReadOnlyList<ReminderRecord> CollectDue(IList<ReminderRecord> reminders, DateTimeOffset now)
    {
        var due = new List<ReminderRecord>();
        for (var index = 0; index < reminders.Count; index++)
        {
            var item = reminders[index];
            if (item.State is "Pending" or "Overdue" && !item.Notified && item.DueAt <= now)
            {
                var ringing = item with { State = "Ringing", Notified = true, LastFiredAt = now };
                reminders[index] = ringing; due.Add(ringing);
            }
        }
        return due;
    }

    public static ReminderRecord Dismiss(ReminderRecord reminder, DateTimeOffset now)
    {
        if (reminder.Recurrence == ReminderRecurrence.None) return reminder with { State = "Dismissed", Notified = true };
        var next = reminder.DueAt;
        var increment = reminder.Recurrence == ReminderRecurrence.Daily ? TimeSpan.FromDays(1) : TimeSpan.FromDays(7);
        do next = next.Add(increment); while (next <= now);
        return reminder with { DueAt = next, State = "Pending", Notified = false, SnoozedFrom = null };
    }

    public static ReminderRecord Snooze(ReminderRecord reminder, DateTimeOffset now, int minutes)
    {
        if (minutes is < 1 or > 1440) throw new ArgumentOutOfRangeException(nameof(minutes));
        return reminder with { DueAt = now.AddMinutes(minutes), State = "Pending", Notified = false, SnoozedFrom = reminder.DueAt };
    }

    public static ReminderRecord MarkOverdue(ReminderRecord reminder, DateTimeOffset now) =>
        reminder.State == "Pending" && !reminder.Notified && reminder.DueAt < now ? reminder with { State = "Overdue" } : reminder;
}

public sealed class ReminderStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public async Task<IReadOnlyList<ReminderRecord>> LoadAsync(CancellationToken token = default)
    {
        if (!File.Exists(path)) return [];
        await using var stream = File.OpenRead(path);
        var loaded = await JsonSerializer.DeserializeAsync<List<ReminderRecord>>(stream, Options, token).ConfigureAwait(false) ?? [];
        return loaded.Select(item => item with
        {
            State = item.State switch { "Ausstehend" => "Pending", "Fällig" => "Overdue", "Erledigt" => "Dismissed", "Abgebrochen" => "Cancelled", "Ringing" => "Overdue", _ => item.State },
            Notified = item.State == "Ringing" ? false : item.Notified
        }).ToArray();
    }

    public async Task SaveAsync(IEnumerable<ReminderRecord> reminders, CancellationToken token = default)
    {
        var directory = Path.GetDirectoryName(path); if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous))
            await JsonSerializer.SerializeAsync(stream, reminders, Options, token).ConfigureAwait(false);
        File.Move(temporary, path, true);
    }
}

public sealed class AlarmPlaybackService : IDisposable
{
    public static readonly IReadOnlyList<string> SoundIds = ["classic", "digital", "gentle", "pulse", "urgent"];
    private readonly object _gate = new();
    private WaveOutEvent? _output;
    private CancellationTokenSource? _previewStop;
    private long _generation;
    public bool IsPlaying { get { lock (_gate) return _output is not null; } }
    public string? LastError { get; private set; }
    public event EventHandler<string>? PlaybackFailed;
    public event EventHandler<string>? PlaybackStateChanged;
    public void Start(string soundId = "classic") => StartCore(soundId, null);
    public void Preview(string soundId = "classic", TimeSpan? duration = null) => StartCore(soundId, duration ?? TimeSpan.FromSeconds(3));
    private void StartCore(string soundId, TimeSpan? duration)
    {
        Stop();
        var generation = Interlocked.Increment(ref _generation);
        LastError = null;
        if (!SoundIds.Contains(soundId, StringComparer.OrdinalIgnoreCase)) soundId = "classic";
        try
        {
            var output = new WaveOutEvent { DesiredLatency = 100 };
            output.PlaybackStopped += Output_PlaybackStopped;
            output.Init(new AlarmWaveProvider(soundId));
            lock (_gate) _output = output;
            output.Play();
            PlaybackStateChanged?.Invoke(this, $"started:{soundId}:generation={generation}");
            if (duration is { } stopAfter)
            {
                var stop = new CancellationTokenSource(); _previewStop = stop;
                _ = Task.Run(async () => { try { await Task.Delay(stopAfter, stop.Token).ConfigureAwait(false); if (!stop.IsCancellationRequested && Volatile.Read(ref _generation) == generation) Stop(); } catch (OperationCanceledException) { } });
            }
        }
        catch (Exception ex) { ReportFailure(ex); Stop(); }
    }
    private void Output_PlaybackStopped(object? sender, StoppedEventArgs args) { if (args.Exception is not null) ReportFailure(args.Exception); }
    private void ReportFailure(Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; try { PlaybackFailed?.Invoke(this, LastError); } catch { /* LastError remains available during dispatcher shutdown. */ } }
    public void Stop()
    {
        var generation = Interlocked.Increment(ref _generation);
        _previewStop?.Cancel(); _previewStop?.Dispose(); _previewStop = null;
        WaveOutEvent? output; lock (_gate) { output = _output; _output = null; }
        if (output is null) { PlaybackStateChanged?.Invoke(this, $"stopped:no-output:generation={generation}"); return; }
        try { output.Stop(); } catch (Exception ex) { ReportFailure(ex); }
        try { output.PlaybackStopped -= Output_PlaybackStopped; output.Dispose(); }
        catch (Exception ex) { ReportFailure(ex); }
        PlaybackStateChanged?.Invoke(this, $"stopped:generation={generation}");
    }
    public void Dispose() => Stop();

    private sealed class AlarmWaveProvider(string soundId) : IWaveProvider
    {
        private long _sample;
        public WaveFormat WaveFormat { get; } = new(44100, 16, 1);
        public int Read(byte[] buffer, int offset, int count)
        {
            var samples = count / 2;
            for (var i = 0; i < samples; i++, _sample++)
            {
                var t = _sample / 44100d; var phase = t % 2d;
                var (frequency, amplitude) = soundId.ToLowerInvariant() switch
                {
                    "digital" => (phase < .13 || phase is > .25 and < .38 ? 1120d : 0d, .32d),
                    "gentle" => (520d + 90d * Math.Sin(t * Math.PI / 2), Math.Min(.20d, (t % 3d) / 10d)),
                    "pulse" => (phase < .48 ? 740d : 0d, .28d),
                    "urgent" => (phase < .18 || phase is > .28 and < .46 || phase is > .56 and < .74 ? 1320d : 0d, .40d),
                    _ => (phase < .45 ? 880d : 660d, phase < .9 ? .27d : 0d)
                };
                var value = frequency == 0 ? 0 : (short)(Math.Sin(2 * Math.PI * frequency * t) * short.MaxValue * amplitude);
                buffer[offset + i * 2] = (byte)value; buffer[offset + i * 2 + 1] = (byte)(value >> 8);
            }
            return samples * 2;
        }
    }
}
