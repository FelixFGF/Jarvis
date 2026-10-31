using Jarvis.Models;

namespace Jarvis.Services;

public sealed class DeferredConversationState
{
    public ChatSession? Pending { get; private set; }
    public bool IsActive { get; private set; }

    public ChatSession Begin(string model, string greetingKey)
    {
        Pending ??= new ChatSession { Model = model, GreetingKey = greetingKey };
        IsActive = true;
        return Pending;
    }

    public bool TryMaterialize(string? firstMessage, out ChatSession? chat)
    {
        chat = null;
        if (!IsActive || Pending is null || string.IsNullOrWhiteSpace(firstMessage)) return false;
        chat = Pending;
        Pending = null;
        IsActive = false;
        return true;
    }

    public void Reset()
    {
        Pending = null;
        IsActive = false;
    }
}
