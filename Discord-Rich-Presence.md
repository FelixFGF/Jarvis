# Discord Rich Presence setup

Jarvis uses Discord's local desktop RPC pipe and does not send prompts, file names,
code, or chat text. The official Jarvis Application ID `1560655725804527676` is
compiled into both Release and Beta builds; users do not enter their own ID.

One Developer Portal step is required for the image:

1. Open application `1560655725804527676` in the Discord Developer Portal.
2. Upload `Assets/Jarvis256.png` (or `Assets/Jarvis.png`) under Rich Presence art
   assets and register the asset key exactly as `jarvis-logo`.
3. Allow Discord time to publish the asset, then enable **Discord Rich Presence**
   in Jarvis while the Discord desktop client is running.

Jarvis always sends a complete activity for `Chatting with Jarvis`, `Talking with
Jarvis`, or `Coding with Jarvis`. Every activity contains the `jarvis-logo` asset
and a `Download Jarvis` button pointing to <https://my-jarvisai.netlify.app>.
Discord may hide an activity owner's own button, so button verification requires
viewing the profile from another Discord account.

Discord controls the application name, colors, typography, button rendering, and
asset approval. RPC cannot color the final period of `Jarvis.` separately, so no
unsupported or misleading color formatting is applied.
