# Jarvis 1.1.0 — local release preparation

## Changes

- Settings navigation uses a compact sidebar with existing general, AI/model,
  personalization, tools/privacy, speech input/output, music and update controls.
- Language switching traverses logical WPF elements safely, including `Run`.
  The localization traversal is snapshotted before text changes mutate inlines.
- Personal instructions have a 4,000-character limit and a dedicated editor.
  They are composed once as lower-priority preferences; the application-owned
  prompt and executor permissions remain authoritative. Legacy `SystemPrompt`
  data is retained for recovery but no longer used as protected instructions.
- Named fallback plans support enable/disable, duplication, deletion, ordering,
  model discovery/manual entry, per-step capabilities, error conditions and
  deadlines. Plans reference the two existing provider configurations (`primary`
  and `fallback`), never duplicate their credentials. Only one plan is enabled.
- Requests use bounded ordered fallback, transient backoff and Retry-After.
  Permanent authentication errors stop. Unknown computer-action outcomes block
  provider changes while still allowing observations on the same provider.
- Optional ElevenLabs voice selection and DPAPI-protected key storage are wired
  into speech output. Local Windows and Piper voices remain available. Automatic
  chat playback is opt-in; voice mode retains its existing behavior.
- Piper remains selected across detected text languages and now honors speed.
  Recognition remains separate and is unchanged.

## Voice implementation and licensing

Kokoro's official [voice catalog](https://huggingface.co/hexgrad/Kokoro-82M/blob/main/VOICES.md)
does not list German. It is not introduced as a German default. Existing Piper
uses Sherpa ONNX CPU inference, keeps the selected model loaded, and generates
sentence chunks. Model downloads remain explicit. Each downloaded model retains
its own licensing requirements; this release does not bundle newly downloaded voices.

ElevenLabs uses the official [stream endpoint](https://elevenlabs.io/docs/api-reference/text-to-speech/stream)
with multilingual v2 and PCM 24 kHz. Each sentence chunk is buffered before
playback, rather than claiming sample-by-sample network playback. Rate/quota
errors are surfaced without logging response bodies or keys. It is a paid/limited
cloud option; only selecting this provider enables text transmission for speech.

## Verification and remaining release gates

Automated suites: `Jarvis.Settings.Tests`, `Jarvis.Persistence.Tests`,
`Jarvis.Beta.Tests`, `Jarvis.Update.Tests`. Settings tests exercise real WPF
trees in both language directions and reproduce the original invalid parent
lookup. Persistence tests include actual local Windows speech playback,
microphone capture and command execution; external provider/Discord/licensing
behavior uses mocks. No live ElevenLabs call was made. This machine has no
English Windows TTS voice or desktop speech recognizer installed.

Full application graphical language-switching and layout checks at all scaling
levels remain manual. Piper voice quality and live cloud latency were not measured.
Resource key parity does not prove every dynamically generated message is localized;
legacy code still contains inline bilingual strings. Plans currently reference two
configured endpoints and apply to main completion requests; there is no task-type
scope editor or arbitrary provider-profile manager in this release preparation.
Capability flags are user-confirmed, not claims inferred from model names.

## Local build

```powershell
dotnet run --project Tests/Jarvis.Settings.Tests -c Release -p:NuGetAudit=false
dotnet run --project Tests/Jarvis.Persistence.Tests -c Release -r win-x64 -p:NuGetAudit=false
dotnet run --project Tests/Jarvis.Beta.Tests -c Release -p:NuGetAudit=false
dotnet run --project Tests/Jarvis.Update.Tests -c Release -p:NuGetAudit=false
powershell -NoProfile -ExecutionPolicy Bypass -File ./Installer/Build-Installer.ps1 -Configuration Beta
```

Output: `Installer/artifacts/Jarvis-Setup.exe`. The official script publishes the
mandatory Beta build and checks activation/configuration markers. The installer
does not itself constitute a signed updater package.

Updater format, repository, trust key, signature checks and installation data
exclusions are unchanged. Creating authenticated updater artifacts still requires
`JARVIS_UPDATE_SIGNING_PRIVATE_KEY_PEM` in a secure release environment:

```powershell
pwsh -NoProfile -File ./Installer/Build-UpdatePackage.ps1 -Configuration Beta -Version 1.1.0
```

The signing variable was unavailable during this work. No replacement trust key,
unsigned trusted updater package, publication, push or remote release change was made.
An actual installed 1.0.8-to-1.1.0 upgrade remains a release validation step.

## Observed build results (22 October 2026)

- Settings suite: 9 localization/preferences/resource checks, 11 fallback checks,
  4 mocked ElevenLabs checks passed. Both resources contain 555 nonempty keys.
- Persistence, Beta and updater suites exited successfully. Live external
  licensing and cloud voice were not exercised.
- Official Beta installer script completed successfully after correcting an
  ambiguous WPF/WinForms ComboBox type reference. Application and setup file
  versions are 1.1.0.0. The script verified the Beta gate in the packaged assembly.
- Fresh output: `G:\Projekte\Jarvis\Jarvis\Installer\artifacts\Jarvis-Setup.exe`
  (233,951,335 bytes), SHA-256
  `1C7E77F1050766CFDDD25415A4A8B8025A3C80428EC757A900B1D88E03A72693`.
- This output is a local build, not a declaration of complete release acceptance.
  No clean installation, full visual walkthrough or signed updater artifact was tested.
