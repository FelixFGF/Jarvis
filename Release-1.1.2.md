# Jarvis 1.1.2

Jarvis 1.1.2 consolidates the completed release work and all intended follow-up improvements into one version.

## ✨ New Features

- Added an optional, resizable developer workspace with a real project tree, persistent file tabs, AvalonEdit syntax highlighting, an embedded browser, file-change inspection and workspace state restoration.
- Added persistent in-app PowerShell and Command Prompt tabs on the chat and workspace sides. Each tab owns a real shell session, working directory, output, error stream, exit state and cancellation lifecycle.
- Added Windows Core Audio tools for system volume and per-application volume, mute, session discovery and verified state changes.
- Added local activity inspection and guarded application cleanup. Jarvis distinguishes foreground applications, games, audio activity, probable media use and unknown states before proposing applications to close.
- Added on-demand multi-monitor vision with one real, labeled in-memory screenshot per display for vision-capable configured models.
- Added critical Windows actions for lock, sign-out, restart and shutdown with three separate, action-bound user confirmations.
- Added optional Discord Rich Presence for chat, voice and coding modes with the built-in Jarvis application ID, branding metadata and a supported `Download Jarvis` button.
- Added selectable STT providers and settings for ElevenLabs, Google Cloud, Groq and supported local Whisper models while keeping TTS provider selection independent.
- Added evidence-based Minecraft diagnosis and controlled Modrinth repair with compatibility filtering, backups, SHA-512 verification and rollback on installation failure.

## ⚡ Improvements

- The workspace can be hidden without leaving an empty column and restores the selected root, mode, tabs, expanded folders and safe browser state when reopened.
- Source editing now includes line numbers, dirty indicators, save/reload support, external-change conflict protection, file search and language-aware highlighting for common programming and configuration formats.
- File Changes now records real filesystem operations, before/after snapshots, accurate added and removed line counts, expandable diffs and guarded rollback.
- The terminal UI now follows a familiar tabbed Windows Terminal layout with a `+` profile menu, direct Enter execution, visible prompts and simpler PowerShell/CMD selection.
- Task completion cards are shown only for substantial verified tool work and use actual file, line, build and test results.
- English and German localization was extended across workspace, terminal, permissions, task summaries, speech and installer surfaces.
- The website, updater, installer, application metadata and user-facing version labels now consistently identify version 1.1.2.

## 🖥️ Computer Use & Automation

- Multi-step desktop requests now use a bounded Plan → Observe → Act → Verify loop with fresh screen evidence between input actions.
- Added `computer_wait_for_state` to wait for an expected window or process to stabilize, then provide a fresh screenshot and window inventory for the next decision.
- Replaced the fixed 500 ms post-input capture with bounded desktop-transition stabilization.
- Raised the bounded execution budget for long launcher workflows while retaining action, cancellation and retry limits.
- Repeated input against the same observation is blocked, while a legitimate repeated key or action is allowed after a fresh screen changes the evidence.
- Visual clicks remain tied to the exact screenshot, target bounds, monitor mapping, current foreground window and measured cursor position.
- Slow launchers, loading views, unexpected dialogs and failed clicks can be re-observed and recovered without treating input dispatch as proof of success.
- A visible launcher is no longer accepted as evidence that the requested game or downstream application has started.

## 🧠 AI, Tools & Agent Behavior

- `plan_task` is a high-priority option for complex dependent work, but is no longer forced through a hard-coded tool choice. The model may answer normally or call the most suitable tool directly when that is clearer.
- A plan is tracking only: every planned step still needs real execution and evidence before completion.
- Tool results, action state, plan state and completion summaries are reconciled before Jarvis reports success.
- Permission modes are evaluated centrally and changes apply to subsequent actions immediately. Allow Everything covers eligible tools while hard security boundaries remain enforced.
- Automatic security review fails closed when the reviewer is unavailable or invalid.
- Provider fallback preserves action history and requires a fresh observation before continuing Computer Use.
- Rich visual responses support native charts, tables, component diagrams, flowcharts, timelines, comparisons, checklists and image galleries with text fallbacks.

## 🐛 Bug Fixes

- Fixed wake-word activation being submitted as the actual request before the user could speak.
- Fixed mixed-language voice follow-up questions and duplicate or unconditional completion prompts.
- Fixed Voice Mode ending after playback instead of returning to listening after a completed task.
- Fixed Auto-Read and manual chat playback so they share the selected provider-independent TTS path, including Windows Speech fallback behavior.
- Added optional local caching for short repeated TTS phrases to reduce repeated ElevenLabs usage without caching user content or secrets.
- Fixed stale Computer Use results leaking into a newer request and prevented unverified desktop actions from producing success claims.
- Fixed workspace and terminal controls remaining disabled until a chat switch after selecting a project root.
- Fixed selector display state so permission and wake-word selectors show only the current value.
- Fixed emoji rendering with Windows emoji font fallback while preserving copied Unicode text.
- Fixed alarm playback lifecycle, duplicate triggers, Stop and Snooze handling, notification state and safe disposal. Five distinct built-in alarm profiles use the same stoppable playback path.
- Fixed provider-specific speech errors being reported as unrelated missing-local-voice failures.

## 🎨 UI & UX

- Refined the dark purple and blue Jarvis design across chat controls, workspace navigation, terminal tabs, file trees, tables, tool activity and completion cards.
- Added compact real change totals above the composer and direct navigation from changed-file cards to the workspace.
- Improved Markdown tables with content-aware column widths, wrapping, contrast, spacing and horizontal scrolling.
- Added native alarm notifications with real reminder title, text, scheduled time, ringing state, Stop and Snooze actions.
- Improved Auto-Read controls and visible status without conflating chat playback with microphone or Voice Mode state.

## 🔒 Security & Reliability

- Critical system actions cannot bypass triple confirmation in any permission mode.
- Computer Use remains one desktop decision per model turn and requires fresh verification before another input action.
- Sensitive foreground contexts block screenshots and credentials, tokens and MFA data remain excluded from Computer Use input.
- Shell jobs remain visible, non-elevated, scoped to an approved working directory and protected by the existing authorization policy.
- Application cleanup protects Jarvis, Windows components, foreground applications, games, probable calls/media and uncertain processes.
- Discord Rich Presence transmits only broad activity categories and never sends prompts, filenames, code or chat contents.
- Installer packaging verifies the Closed Beta activation gate and public client configuration before creating the distributable.

## Verification notes

- `Jarvis.Settings.Tests`, `Jarvis.Persistence.Tests`, `Jarvis.Beta.Tests` and `Jarvis.Update.Tests` passed on the release workstation. They cover settings, localization, tool permissions, agent execution, Computer Use state, file changes, terminal sessions, speech, reminders, alarms, Beta licensing and updater behavior.
- Release and Beta application builds completed with zero compilation warnings and zero errors.
- The final Closed Beta installer is 240,190,623 bytes and has SHA-256 `AA1FC4427C79FAD75914119433C09D1F06FE2F70AD514698287B6FFD87F05A78`.
- English Windows TTS and native Windows desktop recognition integration checks were skipped because those optional Windows language components are not installed on the release workstation.
- External provider calls, Discord presentation, launcher/game workflows and pixel-level UI appearance still require suitable live accounts, applications and desktop surfaces. They are not considered verified by compilation or mocks alone.
- The distributable installer is built from the Beta configuration through `Installer/Build-Installer.ps1` and reports version 1.1.2 / 1.1.2.0.
