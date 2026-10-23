namespace Jarvis.Models;

public sealed class ChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Neuer Chat";
    public string Model { get; set; } = "";
    public string Workspace { get; set; } = "";
    public string GreetingKey { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public List<ChatRecord> Messages { get; set; } = [];
}

public sealed record ChatRecord(string Role, string Text, List<ChatAttachment>? Attachments = null, List<ChatSource>? Sources = null);

public sealed record ChatAttachment(string Name, string Path, string MimeType, long SizeBytes = 0, string PreviewStatus = "metadata-only", double? DurationSeconds = null);

public sealed record ChatSource(string Title, string Location, string Kind, string Status, string Domain = "", string Description = "");
