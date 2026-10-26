using System.Text.Json;
using System.IO;
using Jarvis.Services;

internal static class ReminderChecks
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS: " + name); checks++; }
        var now = new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.FromHours(2));
        var reminder = ReminderRules.Create("Cardo stream", now.AddHours(2), ReminderRecurrence.None, now);
        Check(reminder.DueAt == now.AddHours(2) && reminder.State == "Pending", "Reminder creation preserves the interpreted offset and due time");
        Check(ReminderRules.IsDuplicate([reminder], reminder with { Id = "another" }), "Equivalent reminders are detected before persistence");
        var scheduled = new List<ReminderRecord> { reminder };
        Check(ReminderRules.CollectDue(scheduled, now.AddHours(1)).Count == 0, "Future reminder does not fire early");
        var due = ReminderRules.CollectDue(scheduled, now.AddHours(2));
        Check(due.Count == 1 && scheduled[0].State == "Ringing" && ReminderRules.CollectDue(scheduled, now.AddHours(2)).Count == 0, "Due reminder fires once and is not duplicated");
        var second = ReminderRules.Create("Second alarm", now.AddHours(2), ReminderRecurrence.None, now);
        var simultaneous = new List<ReminderRecord> { reminder, second };
        var simultaneousDue = ReminderRules.CollectDue(simultaneous, now.AddHours(2));
        Check(simultaneousDue.Count == 2 && simultaneousDue.Select(item=>item.Id).Distinct().Count()==2, "Simultaneous alarms retain separate identities while duplicate triggers remain blocked");
        var snoozed = ReminderRules.Snooze(scheduled[0], now.AddHours(2), 5);
        Check(snoozed.DueAt == now.AddHours(2).AddMinutes(5) && !snoozed.Notified && snoozed.State == "Pending", "Snooze schedules the requested new pending occurrence");
        var recurring = ReminderRules.Create("Daily", now.AddMinutes(1), ReminderRecurrence.Daily, now);
        var next = ReminderRules.Dismiss(recurring with { State = "Ringing", Notified = true }, now.AddDays(2));
        Check(next.DueAt > now.AddDays(2) && next.Recurrence == ReminderRecurrence.Daily && !next.Notified, "Recurring reminder advances past the current time");
        Check(AlarmPlaybackService.SoundIds.SequenceEqual(["classic","digital","gentle","pulse","urgent"]), "Five distinct built-in alarm sound profiles are registered");
        using (var playback = new AlarmPlaybackService())
        {
            foreach (var sound in AlarmPlaybackService.SoundIds) { playback.Preview(sound, TimeSpan.FromMilliseconds(60)); await Task.Delay(20); playback.Stop(); }
            Check(!playback.IsPlaying, "All alarm profiles use one stoppable playback lifecycle without overlapping instances");
            playback.Preview("classic", TimeSpan.FromMilliseconds(40)); await Task.Delay(10); playback.Start("digital"); await Task.Delay(70);
            if (playback.LastError is null) Check(playback.IsPlaying, "An expired preview task cannot stop a newer alarm playback generation");
            playback.Stop(); await Task.Delay(20); Check(!playback.IsPlaying, "Stop permanently ends the current alarm playback generation");
        }

        var path = Path.Combine(AppContext.BaseDirectory, "jarvis-reminder-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new ReminderStore(path); await store.SaveAsync([snoozed, next]); var restored = await store.LoadAsync();
            Check(restored.Count == 2 && restored[0].Id == snoozed.Id && restored[1].Recurrence == ReminderRecurrence.Daily, "Reminder persistence restores IDs, state, and recurrence");
            await store.SaveAsync([reminder with { State = "Ringing", Notified = true }]); restored = await store.LoadAsync();
            Check(restored.Single().State == "Overdue" && !restored.Single().Notified, "A persisted ringing state is recovered as a pending overdue alarm instead of orphaned audio state");
        }
        finally { if (File.Exists(path)) File.Delete(path); if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); }

        using var defaultFormJson = JsonDocument.Parse("""{"form_id":"details","title":"Details","questions":[{"id":"loader","label":"Loader","required":true,"options":[{"value":"fabric","label":"Fabric"},{"value":"forge","label":"Forge"}],"allow_other":true}]}""");
        var defaultForm = InteractiveQuestionSchema.Parse(defaultFormJson.RootElement);
        Check(defaultForm.Questions[0].Type == InteractiveQuestionType.SingleChoice && defaultForm.Questions[0].AllowOther, "Interactive questions default to single choice and retain Other");
        using var multiFormJson = JsonDocument.Parse("""{"form_id":"features","title":"Features","questions":[{"id":"items","label":"Choose","type":"multiple_choice","required":false,"options":[{"value":"a","label":"A"},{"value":"b","label":"B"}]}]}""");
        Check(InteractiveQuestionSchema.Parse(multiFormJson.RootElement).Questions[0].Type == InteractiveQuestionType.MultipleChoice, "Multiple selection requires an explicit multiple_choice type");
        Console.WriteLine(checks + " reminder/question checks passed.");
    }
}
