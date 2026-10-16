# ChainShot browser extension

Manifest V3 extension for Chromium browsers (Chrome, Edge, Brave, Vivaldi; Chromium 116+).

1. `chrome://extensions` (or `edge://extensions`) > enable **Developer mode** > **Load unpacked** > select this folder.
   (Store versions: see [docs/publishing-extension.md](../docs/publishing-extension.md).)
2. Click the ChainShot icon, create a case, capture with the button or `Alt+Shift+S`
   (change it at `chrome://extensions/shortcuts`).

## Where captures go

| In the popup | Case | Notes |
| --- | --- | --- |
| **This browser** | `Downloads/ChainShot/<case>-<UTC>/`, created in the popup | the journal is also kept in the extension's storage: **Rewrite journal file** writes it again if the file was moved or deleted |
| **ChainShot app** | the case open in ChainShot (Windows app) or selected with `chainshot.py use` | captures, notes, the tag and *Verify chain* work on that case; see [docs/browser-link.md](../docs/browser-link.md) |

Both are regular ChainShot cases: `python chainshot.py verify <folder>` checks them.

## Privacy and permissions

No server, no telemetry, no network access: see [PRIVACY.md](../PRIVACY.md).

| Permission | Why |
| --- | --- |
| `activeTab` | capture the tab you are on, only after a click or the shortcut |
| `downloads` | write the case files into the Downloads folder |
| `storage` | keep the journal, the tag and the next note on this computer |
| `nativeMessaging` | talk to the ChainShot app on this computer, only when *ChainShot app* is selected |

No host permission: the extension cannot read web pages in the background.

The `key` in `manifest.json` fixes the extension ID (`dcccfnbiglhpbcdhbmokfilfgpfnmikf`) so that the ChainShot app
recognises it; store packages are built without it.

Firefox is not supported yet (planned).
