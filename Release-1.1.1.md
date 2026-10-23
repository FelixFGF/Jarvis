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

- Release build: successful, zero warnings and errors; file version 1.1.1.0.
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
- Final official Beta installer build completed successfully after the last audio
  level fix. `Jarvis-Setup.exe` and its packaged application both report 1.1.1.0.
  The installer is 233,956,480 bytes; SHA-256 is
  `2B233B6DA541DF1FAC0CC878FE69735F26F90A0D9EC40C92C28FBE0FF030C338`.
  Its generated checksum file matches. The local EXE is not Authenticode-signed;
  no signing credential was available or embedded.
