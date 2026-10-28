# Jarvis Setup

## Download

The website can always link to the latest published installer without changing its URL:

`https://github.com/FelixFGF/Jarvis/releases/latest/download/Jarvis-Setup.exe`

The matching SHA-256 file is available at:

`https://github.com/FelixFGF/Jarvis/releases/latest/download/Jarvis-Setup.exe.sha256`

Pushing a version tag such as `v1.1.2` runs the GitHub Actions workflow and publishes the installer and checksum as release assets.

`Build-Installer.ps1` publishes Jarvis with the `Beta` configuration as a self-contained Windows x64 WPF app, compresses that publish output and embeds it in the custom WPF bootstrapper. This is required: the ordinary `Release` configuration does not include Closed Beta activation. The deliverable is one `Jarvis-Setup.exe`; the `.sha256` sidecar is a verification aid, not a runtime dependency.

## Build

Run from this folder or any PowerShell prompt:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\Installer\Build-Installer.ps1 -Configuration Beta -Version 1.1.2
```

Output: `Installer\artifacts\Jarvis-Setup.exe`. The Beta client embeds the Supabase URL and publishable key from `Jarvis.csproj`; the URL can be overridden with `JARVIS_SUPABASE_URL`, and the public key can be overridden with `JARVIS_SUPABASE_PUBLISHABLE_KEY`. Never use a service-role key. A .NET 8 SDK is needed only on the build PC. The installed Jarvis app includes its .NET 8 runtime and does not require the SDK or a separately installed .NET runtime.

## Installer behavior

- Installs per user by default to `%LocalAppData%\Programs\Jarvis`; no administrator rights are requested. A different writable folder can be selected.
- Adds Start menu and optional Desktop shortcuts. The generated PowerShell uninstaller removes only files listed in Jarvis's installation manifest and selected shortcuts. It offers to keep personal data or delete chats, settings, logs and Jarvis's configured local resource folder. The data folder is resolved for the current Windows user, normally `<user profile>\AppData\Local\Jarvis`.
- Checks locally reachable Ollama, LM Studio and BionicGPT-style OpenAI-compatible model endpoints. BionicGPT defaults to `http://localhost:1235/v1`; users can enter another compatible URL, provide a bearer key and load the server's model list. The key is protected with Windows DPAPI for the current Windows account; Jarvis sends it only to the configured endpoint and clears it if that endpoint is changed. Ollama installation is optional and confirmed separately; the installer downloads only from `ollama.com`, verifies the Authenticode chain and the `O=Ollama Inc.` publisher identity, then launches the official interactive setup and waits for Ollama's API to become reachable.
- New installs receive Jarvis's default assistant instructions. During updates, an untouched older default prompt is migrated; a custom user prompt is preserved.
- On launch, setup looks for a registered Jarvis install and the known per-user install folder. A matching install manifest is required before files are updated, while chats/settings/logs remain in the separate user-data directory.
- If the Microsoft VC++ x64 runtime is missing, setup asks before downloading it from the official Microsoft `aka.ms` URL, verifies the Windows trust chain and publisher identity, and launches the official installer. The user can continue without it, but local Whisper speech recognition will then be unavailable.
- If Ollama is running, installed models and a selectable Qwen 3 size range are shown. One RAM-based suggestion is preselected, while the user can choose another model directly. Downloads require separate confirmation, check free disk space, and stream through Ollama's local API. Ollama controls its model directory and GPU/CPU offload. For LM Studio, setup reads both `/api/v1/models` and `/api/v0/models` so downloaded models that are not currently loaded can still be selected.
- Jarvis settings and chat data remain in the current user's resolved local data folder, normally `<user profile>\AppData\Local\Jarvis`; this existing app build does not support relocating that folder. Ollama model data remains in Ollama's configured location (`OLLAMA_MODELS` or the normal Ollama model directory) and is never removed by Jarvis's uninstaller.

## Limits disclosed by the installer

BionicGPT's OpenAI-compatible API requires a bearer API key when authentication is enabled. The installer can now protect and hand off that key to Jarvis for the current Windows user. GPU VRAM and actual offload are backend-specific and cannot be reliably inferred before a backend loads a model; setup reports detected adapters and leaves acceleration verification to the selected backend.

The setup uses the existing published application rather than changing Jarvis's chat or tool behavior. Run the installer UI on Windows to verify actual endpoint combinations and shortcuts; automated build and packaging checks alone cannot validate a clean Windows desktop installation.
