# Jarvis speech-recognition providers

## Implemented providers

- **Windows Native Speech** uses `System.Speech.Recognition.SpeechRecognitionEngine` with a Windows desktop recognizer installed for the selected Jarvis UI language (`de-DE` or `en-US`). It does not invoke `Win + H`, does not need a Jarvis API key, and submits the captured WAV to that installed recognizer. Offline behavior must be verified for the particular Windows recognizer installed on the target PC.
- **Whisper** keeps the existing Whisper.net path, selected microphone support, multilingual models, and offline transcription after the model download.

The newer `Windows.Media.SpeechRecognition` dictation API is not used because Microsoft requires package identity for it. Jarvis is currently distributed as an unpackaged, self-contained WPF application. Windows voice typing (`Win + H`) is a user-facing shell experience rather than a dependable embedded transcription API.

## Requirements and limitations

Windows Native Speech is shown as ready only when Windows reports an installed recognizer matching the current Jarvis language. Install speech-language components through **Windows Settings → Time & language → Language & region → Language options**. Microphone access must also be enabled under Windows privacy settings.

The Windows desktop provider and Whisper both use Jarvis's existing WASAPI capture. Jarvis converts the recording to mono, 16 kHz, 16-bit PCM before native recognition. Selecting one provider never silently changes to the other; errors explain which component is unavailable.

## Fair comparison procedure

Use the same microphone, distance, room, input level, Jarvis language, and phrases for both providers. Do not retain or upload recordings beyond this local test.

Test each phrase three times:

| Category | German example | English example |
|---|---|---|
| Short command | „Öffne die Einstellungen.“ | “Open the settings.” |
| Longer dictation | „Jarvis soll diesen längeren Satz erkennen und vollständig in das Eingabefeld einfügen.“ | “Jarvis should recognize this longer sentence and place it completely in the input field.” |
| Punctuation and numbers | „Termin am 24. Oktober um 15 Uhr 30.“ | “Meeting on October 24 at 3:30 PM.” |
| Background noise | Repeat the long phrase with a steady, documented noise source. | Repeat the long phrase with the same noise source. |

For each run, record recognition latency from end of capture to returned text, word errors against the reference phrase, peak Jarvis working set, CPU time, and whether recognition succeeds with networking disabled. Windows Native Speech must not be described as better than Whisper until this procedure has been performed on the target PC with its installed recognizer.
