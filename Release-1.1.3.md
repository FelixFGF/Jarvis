# Jarvis 1.1.3

Jarvis 1.1.3 makes everyday chat faster, improves control over model reasoning, and adds safer conversation and content workflows.

## Added

- Added persistent **Fast**, **Balanced**, and **Deep Thinking** modes. Supported providers receive their native reasoning controls, while unsupported models continue without incompatible request parameters.
- Added deferred conversation creation: a new chat is saved only after the first meaningful message is submitted, including requests started through **Hey Jarvis**.
- Added copy and inline edit actions for user messages. Historical edits preserve conversation consistency without replaying previous tool side effects.
- Added a dedicated, localized delete-conversation confirmation dialog with safe handling for active, inactive, and last conversations.
- Added native SVG creation, update, preview, and chat rendering with professional design guidance for diagrams, charts, timelines, architecture views, and other visual explanations.
- Added centralized storage paths for growing Jarvis data, with safe migration of existing conversations and generated SVG files.

## Improved

- Simple greetings and casual messages now use a compact fast path with minimal history, no tool schemas, no planning, and low reasoning overhead.
- Tool definitions and specialist prompt guidance are selected by request complexity instead of being attached to every model request.
- Local and OpenAI-compatible providers now use provider-aware request, streaming-inactivity, and tool timeouts, allowing slow but active local models more time to respond.
- Context-limit recovery can retry safe chat requests with reduced history and compact instructions instead of leaving the conversation waiting indefinitely.
- Voice-mode prompts favor concise, natural spoken responses while preserving essential results, warnings, and the user's language.
- Discord Rich Presence keeps the user's enabled preference across temporary disconnects and activity changes, and reconnects without creating competing clients.
- SVG generation now applies a reusable visual design system with deliberate hierarchy, spacing, typography, accessible palettes, dark-interface compatibility, and layout-specific guidance.
- Persistent data locations are resolved through one storage service, and unavailable storage is reported instead of silently redirecting large files.

## Fixed

- Fixed the Thinking selector so its icon, label, checkmark, tooltip, and support state update immediately and remain synchronized with outgoing requests.
- Fixed empty conversations appearing after repeatedly selecting **New Chat**, cancelling wake-word recognition, or leaving an unsent draft.
- Fixed local-model requests receiving oversized system prompts and unrelated tool schemas that could exceed smaller context windows.
- Fixed timeout errors being confused with user cancellation and prevented unsafe retries from duplicating messages or tool side effects.
- Fixed Discord Rich Presence disappearing after navigation, mode changes, or a temporary RPC connection failure.
- Fixed conversation deletion leaving stale active-chat content or creating an unwanted empty history entry.
- Fixed user-message editing from exposing later responses as if they had been generated from the edited text.
- Fixed monochrome emoji output in chat by rendering Unicode emoji sequences through a color-emoji-capable WPF path while preserving the original text for copying.
- Fixed generated SVG files becoming inaccessible after storage migration.

## Technical Improvements

- Added focused regression coverage for thinking-mode persistence and provider mapping, deferred chat creation, safe deletion and editing, provider timeouts, request complexity, centralized storage migration, SVG prompt selection, and emoji sequence handling.
- Kept version metadata consistent at `1.1.3` / `1.1.3.0` across the application, updater, shared components, manifest, and release metadata.
