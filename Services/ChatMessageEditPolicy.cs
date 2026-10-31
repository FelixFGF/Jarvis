using Jarvis.Models;

namespace Jarvis.Services;

public sealed record ChatMessageEditResult(bool Changed, IReadOnlyList<ChatRecord> Messages, int RemovedMessages);

public static class ChatMessageEditPolicy
{
    public static string CopyText(ChatRecord message) => message.Text;

    public static ChatMessageEditResult Apply(IReadOnlyList<ChatRecord> messages, int targetIndex, string editedText)
    {
        if (targetIndex < 0 || targetIndex >= messages.Count) throw new ArgumentOutOfRangeException(nameof(targetIndex));
        if (!messages[targetIndex].Role.Equals("user", StringComparison.Ordinal)) throw new InvalidOperationException("Only user messages can be edited.");
        editedText = editedText.Trim();
        if (editedText.Length == 0) throw new ArgumentException("A user message cannot be empty.", nameof(editedText));
        if (messages[targetIndex].Text.Equals(editedText, StringComparison.Ordinal)) return new(false, messages.ToArray(), 0);

        // The edited turn becomes the new end of the conversation. Keeping later
        // assistant/tool records would make them look as if they were generated
        // from text the model never saw and could expose stale side-effect results.
        var updated = messages.Take(targetIndex + 1).ToArray();
        updated[targetIndex] = updated[targetIndex] with { Text = editedText, StaleAfterEdit = false };
        return new(true, updated, messages.Count - targetIndex - 1);
    }
}
