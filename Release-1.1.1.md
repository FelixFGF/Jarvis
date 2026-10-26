# Jarvis 1.1.1

## Speech investigation

The path is WASAPI capture (device-native sample rate/float or PCM), WAV storage,
mono conversion, resampling to 16 kHz PCM16, Whisper decoding, optional leading
wake invocation removal, then insertion into the chat prompt. Existing voice mode
retains its explicit automatic send behavior; ordinary dictation remains unsent.

Confirmed defects corrected:

- WASAPI `WaveFormatExtensible` IEEE-float samples were treated as integer PCM
  by the level meter. A 0.001 noise sample could cross the speech gate. The float
  subtype is now identified explicitly; a deterministic regression test checks
  the exact measured level. This affects endpointing, not the WAV sample bytes.

- The Whisper provider dropped `SpeechTranscriptionRequest.LanguageTag` both on
  preload and transcription. It now maps de-DE to de and en-US to en and forwards
  the value. Previously, short phrases used automatic language detection.
- Voice endpointing ignored the saved silence duration and always used 600 ms.
  It now honors the existing bounded 350–1500 ms setting.
- Wake-prefix removal accepted unrelated names (including Chavez) and had no word
  boundary. It now removes only a leading Jarvis/Jervis/Jarwis invocation with a
  delimiter. No spelling replacement or semantic correction is added.
- Beam search widened from one candidate to five. This is a decoding tradeoff,
  not a measured accuracy guarantee; it may increase CPU latency.

Audio conversion is exposed for deterministic regression tests. Synthetic 16 kHz,
44.1 kHz and 48 kHz mono/stereo signals preserve duration and amplitude after
conversion. No arbitrary gain boost, denoising or guessed spelling changes are used.
The user's original failing recording was not available; the exact origin of
“Hinausgeht” and any improvement in word error rate remain unverified.

## Engines and hardware

Existing multilingual Whisper tiny/base/small/medium/turbo choices remain intact;
no English-only `.en` model is introduced. Larger models cost more RAM and CPU time;
no existing model choice is silently changed or downloaded. Windows desktop speech
remains selectable when its recognizer is installed. It is unavailable on this test
machine. No new recognition dependency or cloud service is introduced.

The packaged Whisper.net.Runtime 1.8.1 is CPU based, so operation does not depend on
NVIDIA, AMD or Intel GPU drivers. GPU acceleration is **not implemented or verified**
by this change. The project's runtime supports separate CUDA/Vulkan packages;
cross-vendor GPU acceleration would require an explicit tested runtime addition,
not merely a model-setting switch. Hardware enumeration through CIM was denied in
this environment. See the [runtime documentation](https://www.nuget.org/packages/Whisper.net.Runtime/1.8.1)
and [Whisper model documentation](https://github.com/openai/whisper#available-models-and-languages).

## Fallback editor

The main list contains friendly primary/backup provider labels, editable discovered
model choices and a simple takeover switch. Expanded row details hold capabilities,
specific failure conditions and timeout. The existing once-per-step routing,
capability checks, cancellation and unknown-action protection are unchanged.
“Validate plan” checks structure, displays order and missing tool-capability warnings,
and explains that no fallback remains after the last step. The separate connection
test verifies listed model availability, without claiming inference capabilities.
Existing provider references and stored flags remain compatible. German/English
resources include new controls and expandable headers.

## Verification and packaging

Run the existing Settings, Persistence, Beta and Update test executables. Settings
tests cover switching actual WPF trees in both directions, advanced headers,
resource parity, fallback flag persistence and routing safety. Persistence includes
synthetic audio conversion, exact transcript preservation, language mapping,
unsupported channels, microphone capture and existing recognition error handling.
No real speech accuracy comparison, live GPU inference or graphical walkthrough
of the full authenticated app is claimed.

Build Release and Beta, then use the official installer script with `-Configuration
Beta`. The distributable remains the mandatory Closed Beta build. Version metadata
is 1.1.1 / 1.1.1.0. Output: `Installer/artifacts/Jarvis-Setup.exe`.
No remote release, push, database change or signing-key change is authorized here.

## Observed test results

- Release and Beta builds: successful with zero errors. NuGet vulnerability metadata
  produced two NU1900 warnings because the NuGet service index was unavailable;
  application compilation itself produced no warning. File version is 1.1.1.0.
- Settings: 11 localization/preferences checks, 14 fallback checks and 4 mocked
  cloud-voice checks passed. Both language resources contain 570 matching keys.
- Persistence: passed, including six synthetic audio conversions, wake-text
  boundary checks, provider language mapping, real microphone capture, Windows
  TTS playback and command-process tests. English Windows TTS and native desktop
  recognition were skipped because their Windows components are not installed.
- Beta and updater suites passed (mocked network; real local crypto/file checks).
- Initial test compilation missed a System.IO import; initial application build
  detected an unclosed XAML cell template. Both were corrected and tests/builds
  rerun successfully. No unresolved compilation failure remains.
- Final official Beta installer build completed successfully after the advanced
  control and Minecraft-diagnostics additions. `Jarvis-Setup.exe` reports file
  version 1.1.1.0 and product version 1.1.1. The installer is 234,997,877 bytes;
  SHA-256 is
  `4E4FF5ADF6278C0D5E3CDFB45CB6CFFA19AA3127036C0C28B567F5A7370BE114`.
  Its generated checksum file matches. The local EXE is not Authenticode-signed;
  no signing credential was available or embedded.

## Advanced agent and repair additions

- Added dedicated native tools for opening approved files in Notepad, opening visible PowerShell/CMD windows in an approved working directory, waiting for a window, and restoring, minimizing, maximizing or resizing a uniquely identified window with result verification.
- Allow Everything is evaluated on each tool action, including ordinary one-time terminal/window actions. Destructive and security-sensitive operations remain hard confirmation boundaries. Tool activity remains independent from permission decisions.
- Added evidence-based Minecraft instance discovery and crash analysis for launcher instance layouts, latest logs, crash reports, Java incompatibility, missing dependencies, mixin failures, memory failures and duplicate mod versions. Controlled repair backups are restricted to files inside the selected instance.
- Added exact Modrinth project/version lookup for Minecraft version and loader with dependency, HTTPS JAR, size and SHA-512 metadata.
- Added in-memory task-scoped file-change tracking with actual added/removed line counts and backup/rollback metadata. Existing create-folder and write-file tools now emit change IDs.
- Fixed manual text-to-speech error reporting so ElevenLabs errors retain their cloud-provider category rather than being described as missing local playback.
- Added a native WebView2 browser page with address navigation, back/forward/reload, visible loading errors and model tools that navigate and verify the current URL/title. Its isolated profile disables password saving and does not expose cookies or credentials to model tools.
- Added separate Files and File Changes tabs. The Files tree loads folders lazily and previews bounded text files; File Changes persists actual tracked mutations under the per-user Jarvis data directory and displays aggregate added/removed lines.
- Added opt-in, event-driven foreground activity history. It keeps at most 100 process/title/timestamp entries locally and never records keys, typed text, screenshots, audio or browser history.
- Pending eligible approvals now re-evaluate when the permission mode changes during a task; hard security boundaries remain human-confirmed.

## Integrated developer workspace completion

- Chat and workspace now share one resizable view. The right side contains persistent
  file tabs, an editable AvalonEdit source editor, a lazy project tree, embedded
  browser tabs and a separate changes tab. The old standalone Browser and Files
  pages were removed so navigation and tools use the same workspace instance.
- Editor state persists the selected root, open documents, active document, safe
  browser URLs, expanded project folders and active workspace view. Files use their real contents, line
  numbers, dirty indicators, explicit save/reload actions and external-change
  conflict protection. Users can navigate by path and search both the project and
  the active document. The syntax catalog covers Python, Java, JavaScript,
  TypeScript, C#, C/C++, HTML, CSS/SCSS, JSON, XML, YAML, Markdown, PowerShell,
  SQL, Java properties, INI/configuration and common shell scripts. AvalonEdit
  6.3.1.120 is bundled; no runtime syntax download is required.
- Browser tools can navigate, report state, read bounded visible page text, click
  a precise selector, fill an approved field, scroll, open/select tabs and wait
  for bounded navigation completion. Password saving and
  general autofill remain disabled. Returned page content remains untrusted.
- File changes persist real before/after snapshots where safe, use a line-level
  diff for exact green additions/red removals, show aggregate counts and provide
  guarded rollback. Rollback refuses to delete or overwrite files changed after
  the recorded operation.
- Substantial tasks with real file mutations receive a compact completion card in
  chat with actual file and line totals, per-file links and an Inspect changes
  action. A compact net change pill also remains directly above the composer and
  opens the Changes workspace. Simple chat replies do not receive an unnecessary card.
- The Minecraft repair tool is now a real, separately approved operation. It
  requires diagnosis evidence, selects an exact Minecraft/loader-compatible
  Modrinth version, creates a ZIP backup, verifies size, SHA-512 and mod-JAR
  structure, disables only the selected old JAR and restores it if installation
  fails. A successful download does not claim that Minecraft was launched.

Remaining limits: runtime gameplay verification still needs a real Minecraft
launch; background audio presence and periodic unattended screenshots are not
implemented. Foreground application history remains opt-in and records no typed
content. The WPF client passed a startup smoke test, but the isolated automation
environment could not visually inspect or click the native window.

## Flexible workspace and integrated terminals

- The workspace is now a persisted optional panel. Hidden mode returns all width
  to chat; split mode remains resizable; Files, Browser, Changes and Terminal can
  use the complete content area without destroying chat or open workspace state.
- The workspace header now accepts, validates and browses for an explicit project
  root. The tree, relative operations, editor and terminal default directory share
  that root. Jarvis no longer silently treats its source checkout as the project.
- Files and Browser navigation use maximized workspace modes and can return to the
  normal split layout. The existing editor tabs, browser tabs, expanded folders
  and selected paths remain persisted.
- Real persistent PowerShell and CMD sessions are available in both the chat side
  and the workspace Terminal tab. Sessions capture standard output and error,
  report exit codes, reject overlapping commands, support process-tree interrupt,
  keep their effective working directory visible and share the existing approval
  policy and visible tool activity.
- Completion cards are now policy-gated: normal conversation has no task card;
  real file mutations or meaningful completed tools can produce completed,
  partial or failed summaries from actual results.
- Automated tests executed both shells, output capture, working-directory
  validation, exit codes, cancellation, duplicate-command rejection, display
  clearing, layout state and completion-card policy. The Beta WPF executable was
  started visibly, remained responsive and closed cleanly. Pixel-level manual UI
  inspection was not available in the automation environment.
