using Jarvis.Models;

namespace Jarvis.Services;

public sealed record ChatDeletionPlan(
    ChatSession Target,
    IReadOnlyList<ChatSession> RemainingChats,
    IReadOnlyList<ChatSession> RemainingOpenTabs,
    ChatSession? NextActiveChat,
    bool DeletesActiveChat);

public static class ChatDeletionPolicy
{
    public static ChatDeletionPlan? Create(
        IReadOnlyList<ChatSession> chats,
        IReadOnlyList<ChatSession> openTabs,
        ChatSession? activeChat,
        ChatSession target,
        bool confirmed)
    {
        if (!confirmed || !chats.Any(chat => chat.Id == target.Id)) return null;
        var remainingChats = chats.Where(chat => chat.Id != target.Id).ToArray();
        var remainingTabs = openTabs.Where(chat => chat.Id != target.Id).ToArray();
        var deletesActive = activeChat?.Id == target.Id;
        var next = deletesActive ? remainingTabs.FirstOrDefault() ?? remainingChats.FirstOrDefault() : activeChat;
        return new ChatDeletionPlan(target, remainingChats, remainingTabs, next, deletesActive);
    }
}
