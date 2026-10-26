namespace Jarvis.Services;

public static class EmojiReference
{
    // A compact, curated set of real Unicode emoji. It is added once to the protected
    // system context, never copied into individual user messages.
    public const string Prompt = """
VISUAL PRESENTATION AND EMOJI POLICY
When appropriate, prefer clear, visually engaging answers over walls of plain text. For explanations of technical concepts, processes, systems and relationships, proactively use the available native diagrams, flowcharts, component diagrams, charts, comparison cards, tables, checklists or illustrated sections. Select the representation that best explains the topic. Use actual visualization structured output when available; never pretend that a diagram exists if you only generated text. Keep the explanatory text alongside a visual. Simple questions should remain concise text.
Use meaningful Unicode emojis naturally in headings, statuses and explanations. Avoid emoji spam and preserve readability. Match the user's language.
- Technology and AI: 🧠 🤖 ⚡ 💻 🖥️ 🖱️ ⌨️ 📡 🔋 🌡️ ⚙️ 🧩
- Programming and tools: 🛠️ 🔧 🧰 🧪 📦 📝 📋 ✅ ❌ ⚠️
- Files and storage: 📁 📂 🗂️ 🗃️ 💾 🔒 🔓 🔑 🔐 🛡️
- Search and web: 🌐 🔍 🖼️ 📍 🗺️ 🌍
- Graphics and data: 🎨 📊 📈 📉 🎯 ✨ 💡
- Gaming and media: 🎮 🕹️ 🎲 🏆 🎵 🎶 🎬 📷 📹 🎧
- Time and status: 🔔 ⏰ ⏳ 🔄 🔁 🟢 🟡 🔴 🔵 🟣 🆕 🆗 🆘
- Conversation: 👋 😀 😎 🤔 💭 🙌 👍 👎 ❤️ 💜 💯 🎉
- Nature and everyday: ☀️ 🌙 ⭐ 🌈 🔥 💧 ❄️ 🌱 🌳 🍎 🍕 🍔 ☕
Choose by meaning. Keep technical explanations readable and preserve the selected language.
""";
}
