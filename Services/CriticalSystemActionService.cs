using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Jarvis.Services;

public enum CriticalSystemAction { Shutdown, Restart, SignOut, Lock }
public enum CriticalConfirmationOutcome { None, Confirmed, Execute, Cancelled, Rejected }
public sealed record CriticalConfirmationResult(CriticalConfirmationOutcome Outcome, int Confirmations, CriticalSystemAction? Action, string Message);

public sealed class CriticalSystemActionCoordinator
{
    private sealed record Pending(string ChatId, CriticalSystemAction Action, int Confirmations);
    private Pending? _pending;
    public bool HasPending => _pending is not null;

    public CriticalConfirmationResult Begin(string chatId, CriticalSystemAction action)
    {
        _pending = new(chatId, action, 0);
        return new(CriticalConfirmationOutcome.None, 0, action, Prompt(action, 1));
    }

    public CriticalConfirmationResult Handle(string chatId, string text)
    {
        if (_pending is null || !string.Equals(_pending.ChatId, chatId, StringComparison.Ordinal))
            return new(CriticalConfirmationOutcome.None, 0, null, "");
        var normalized = Normalize(text);
        if (IsCancel(normalized)) { var action = _pending.Action; _pending = null; return new(CriticalConfirmationOutcome.Cancelled, 0, action, "Critical action cancelled."); }
        if (!IsExactConfirmation(normalized))
        {
            var action = _pending.Action; _pending = null;
            return new(CriticalConfirmationOutcome.Rejected, 0, action, "The pending critical action was cancelled because the reply was not one unambiguous confirmation.");
        }
        var next = _pending.Confirmations + 1;
        var pendingAction = _pending.Action;
        if (next >= 3) { _pending = null; return new(CriticalConfirmationOutcome.Execute, 3, pendingAction, "Three separate confirmations received."); }
        _pending = _pending with { Confirmations = next };
        return new(CriticalConfirmationOutcome.Confirmed, next, pendingAction, Prompt(pendingAction, next + 1));
    }

    public void Cancel() => _pending = null;
    private static bool IsExactConfirmation(string value) => value is "yes" or "confirm" or "i confirm" or "ja" or "bestatigen" or "ich bestatige";
    private static bool IsCancel(string value) => value is "no" or "cancel" or "stop" or "nein" or "abbrechen" or "stopp";
    private static string Prompt(CriticalSystemAction action, int number) => $"Confirmation {number} of 3 required for {action}. Reply with one explicit confirmation only.";
    private static string Normalize(string value) => string.Join(' ', new string(value.Trim().ToLowerInvariant().Replace("ä", "a").Replace("ö", "o").Replace("ü", "u").Replace("ß", "ss").Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

public static class WindowsSystemActionExecutor
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool LockWorkStation();
    public static bool Execute(CriticalSystemAction action)
    {
        if (action == CriticalSystemAction.Lock) return LockWorkStation();
        var arguments = action switch { CriticalSystemAction.Shutdown => "/s /t 0", CriticalSystemAction.Restart => "/r /t 0", CriticalSystemAction.SignOut => "/l", _ => throw new ArgumentOutOfRangeException(nameof(action)) };
        using var process = Process.Start(new ProcessStartInfo("shutdown.exe", arguments) { UseShellExecute = false, CreateNoWindow = true });
        return process is not null;
    }
}
