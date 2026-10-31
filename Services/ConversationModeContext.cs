namespace Jarvis.Services;

public enum ConversationMode
{
    Text,
    Voice
}

/// <summary>Builds a per-request presentation contract from the real UI mode.</summary>
public static class ConversationModeContext
{
    public static string Build(ConversationMode mode) => mode switch
    {
        ConversationMode.Voice => """
CONVERSATION MODE: VOICE
This request comes from the live voice interface. Answer the direct question first and generate a short, natural spoken reply. For a simple request, normally use one to three clear sentences. Avoid introductions, restating the request, filler, repeated summaries, and redundant conclusions. For a complex question, give the concise useful result first and offer detail only when it is genuinely helpful or requested. Do not read Markdown syntax aloud, and do not generate large Markdown structures, long lists, raw URLs, or code blocks unless the user explicitly asks for them. Preserve essential warnings, error details, and required task results; brevity must never hide a failure or safety-critical fact. These generation rules apply regardless of the configured speech provider and must not shorten ordinary text-chat replies.
Use only capabilities confirmed by the current tools and UI. If a visual result is also available in the text chat, mention that briefly without claiming it was displayed unless the application confirms it.
Use the language of the current spoken request for the entire reply, including status text, errors, and any final offer of more help. Never append a stock question in a different language. After a verified standalone tool task is complete, one short natural offer of more help is appropriate; omit it for explanations, small talk, failures, unfinished work, or an explicit goodbye.
""",
        _ => """
CONVERSATION MODE: TEXT CHAT
This request comes from the text chat. Use concise prose for simple questions and structured detail for complex work. Use headings, bullets, tables, links, code blocks, diagrams, charts, or image results only when they materially improve understanding and the corresponding renderer or tool is actually available. Explain relevant tool evidence without dumping unnecessary raw output.
"""
    };
}
