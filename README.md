# Jarvis for Windows

<p align="center">
  <img src="https://my-jarvisai.netlify.app/assets/jarvis-logo.png" alt="Jarvis Logo" width="160">
</p>

<h1 align="center">Jarvis</h1>

<p align="center">
  <strong>Your AI. Your Desktop. Your Control.</strong>
  <br>
  A native Windows desktop assistant built to make everyday tasks easier with configurable AI models, local speech processing, and practical desktop tools.
</p>

<p align="center">
  <a href="https://my-jarvisai.netlify.app/">Website</a> •
  <a href="Release-1.1.2.md">Release notes 1.1.2</a> •
  <a href="https://github.com/FelixFGF/Jarvis/issues">Report a Bug</a> •
  <a href="https://dsc.gg/local-jarvis">Request a Feature</a>
</p>

---

## ✨ Overview

**Jarvis** is a native Windows desktop AI assistant developed by **FelixFGF** using .NET 8 and WPF.

Unlike browser-based interfaces, Jarvis runs as a Windows desktop application without embedding a web frontend. Connect it to a compatible AI server, choose your preferred model, and use its tools to interact with files, applications, and your desktop.

Jarvis is designed with local AI workflows in mind. You choose the configured AI provider and endpoint; local processing is available for supported speech features.

**The application is the interface and tool layer. The AI model is supplied by your configured provider.**

## 🚀 Features

### 💬 AI Chat
- Native Windows desktop interface with a dark theme.
- Multiple conversations and independently managed chat tabs.
- Per-chat model selection and configurable system prompts.
- Markdown rendering, tables, links, and copyable code blocks.
- File and image attachments for supported models.
- Cancel, copy, and regenerate assistant responses.

### 🧠 Flexible AI Providers
- Connect to LM Studio and other OpenAI-compatible API servers.
- Discover available models and refresh the model list.
- Select downloaded or unloaded models in supported LM Studio setups.
- Configure your endpoint and model directly in Settings.
- Continue using the application interface even when no AI server is running.

### 🖥️ Desktop Tools
- Launch Windows applications.
- Open files, folders, and web links.
- Search filenames and read supported text files.
- Create folders and write files inside approved workspaces.
- View local task history and action logs.

File modifications require confirmation. Existing symbolic links are rejected, and existing files are backed up before being overwritten. Jarvis does not currently provide unrestricted shell access or a file-deletion tool.

### 🎙️ Offline Speech Recognition
- Record speech directly from the chat composer.
- Select an audio input device or use the Windows default.
- View live microphone activity.
- Transcribe speech into an editable message.
- Run Whisper-based speech recognition locally using supported models.

Speech recognition does not automatically send your message. The Microsoft Visual C++ 2022 x64 Redistributable is required for the native CPU runtime.

### 🔊 Text-to-Speech
- Use voices installed in Windows.
- Optionally use a local Piper neural voice for German.
- Start playback before an entire long response has been synthesized.
- Keep supported speech synthesis available offline after setup.

### 🗂️ Memory and Conversations
- Save and search conversations locally.
- Rename, search, and delete conversations.
- Manage persistent memories explicitly.
- Only confirmed memories are included in the model context.
- Store local profile information and an optional avatar.

## 🛠️ Requirements

- Windows x64
- .NET 8 SDK for development
- A compatible AI server and model for AI chat
- Microsoft Visual C++ 2022 x64 Redistributable for the Whisper CPU runtime
- Internet access for downloading optional models or accessing online resources

Jarvis itself does not require a cloud AI service. However, the privacy and connectivity characteristics of AI requests depend on the endpoint you configure.

## 📥 Getting Started

### Option 1: Run from Source

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Clone the repository:

   ```powershell
   git clone https://github.com/FelixFGF/Jarvis.git
   cd Jarvis
   ```

3. Run the application:

   ```powershell
   dotnet run --project Jarvis.csproj
   ```

### Option 2: Run a Published Build

If you already have a published build, launch `Jarvis.exe` from the release folder.

Keep the entire published folder together because the current build includes adjacent runtime files.

> **Note:** A published executable is not necessarily a complete installer. Check the repository's available releases for the latest distributable package.

## ⚙️ Configure Your AI Model

1. Start your local AI server, for example LM Studio.
2. Open Jarvis and navigate to **Settings**.
3. Set the API base URL. The default is:

   ```text
   http://localhost:1234/v1
   ```

4. Enter the model name and test the connection.
5. Save your settings.
6. Select a model in the chat composer and start chatting.

Jarvis discovers models through the configured server. For LM Studio, it can also read the local `/api/v1/models` catalog, including downloaded and unloaded models.

When a supported unloaded LM Studio model is selected, Jarvis can request that it be loaded before retrying the chat.

Other OpenAI-compatible servers use the `/models` endpoint for model discovery. Tool calling requires support for the OpenAI Chat Completions `tools` format.

## 🔒 Privacy and Security

Jarvis is designed to keep user control at the center of the desktop experience.

- **Configurable endpoints:** You decide which AI server receives your requests.
- **Local speech processing:** Supported Whisper and Piper models can run offline.
- **Explicit memory:** Only confirmed memory entries are included in the model context.
- **Workspace restrictions:** File tools operate within user-selected roots.
- **Write confirmations:** File creation and modification require approval.
- **No unrestricted shell tool:** The current agent toolset does not expose arbitrary shell execution.

### What Data Is Sent?

Chat messages, attached files, confirmed memories, and content read by agent tools may be sent to the configured AI endpoint when needed for a request.

Jarvis also queries the configured server for model discovery during startup and after connection settings are saved.

API keys are not required or stored by the current implementation. Do not assume that every compatible endpoint processes data locally; verify your provider configuration before sending sensitive information.

### Attachment Limits

| Limit | Maximum |
|---|---:|
| Files per message | 10 |
| Individual file size | 10 MB |
| Total attachment size | 20 MB |
| Individual text file size | 1 MB |

Image understanding requires a model that supports vision.

## 🏗️ Build from Source

Build a Release configuration:

```powershell
dotnet build Jarvis.csproj -c Release
```

Publish a self-contained Windows x64 build:

```powershell
dotnet publish Jarvis.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o ".\artifacts\Jarvis-win-x64"
```

The published output includes the runtime files required by this build.

### Run Persistence Tests

```powershell
dotnet run --project Tests\Jarvis.Persistence.Tests\Jarvis.Persistence.Tests.csproj -c Release
```

The tests cover chat persistence, schema migration, attachment references, unsupported schema versions, audio-device enumeration, and missing Whisper model handling.

Actual microphone capture, transcription accuracy, and device switching should also be tested on a Windows PC with a microphone.

## 🚧 Development Status

Jarvis is actively being developed. The following features are not yet implemented or are still incomplete:

- Token-by-token streaming responses
- Image generation
- Mermaid and graph visualization
- Syntax-highlighted code blocks
- Configurable tool-confirmation modes
- Automatic persistent reminders while Jarvis is closed

One-shot local reminders are surfaced while Jarvis is running.

## 🐛 Bug Reports and Feature Requests

Found a bug, encountered an error, or have an idea for a new feature?

Please open an issue on GitHub and include steps to reproduce the problem, relevant screenshots, and error logs where possible.

**[Report a Bug or Request a Feature](https://github.com/FelixFGF/Jarvis/issues)**

Your feedback helps make Jarvis better. 💜

## 🌐 Community and Links

- **Website:** https://my-jarvisai.netlify.app/
- **GitHub Repository:** https://github.com/FelixFGF/Jarvis
- **Issues & Feedback:** https://github.com/FelixFGF/Jarvis/issues
- **Discord**: https://dsc.gg/local-jarvis

---

<p align="center">
  <strong>Jarvis — Your AI. Your Desktop. Your Control.</strong>
  <br>
  Developed by FelixFGF 💜
</p>
