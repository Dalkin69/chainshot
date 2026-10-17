# ChainShot browser extension

Manifest V3 extension for Chromium browsers (Chrome, Edge, Brave, Vivaldi; Chromium 116+) and Firefox 140+.
This folder is the Chromium version; `python tools/build_extension.py` builds the store and Firefox packages.

1. `chrome://extensions` (or `edge://extensions`) > enable **Developer mode** > **Load unpacked** > select this folder.
   (Store versions: see [docs/publishing-extension.md](../docs/publishing-extension.md).)
   Firefox: `about:debugging` > *This Firefox* > *Load Temporary Add-on* > the Firefox zip of a release.
2. Click the ChainShot icon, create a case, capture with the button or `Alt+Shift+S`
   (change it at `chrome://extensions/shortcuts`). **Full page** scrolls and stitches the whole page;
   **Page archive** adds an MHTML copy of the page (Chromium only).

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
| `scripting` | full page: scroll the captured page part by part, then restore it |
| `pageCapture` (optional) | MHTML page archive, asked when you tick *Page archive* (Chromium) |
| `downloads` | write the case files into the Downloads folder |
| `storage` | keep the journal, the tag and the next note on this computer |
| `nativeMessaging` | talk to the ChainShot app on this computer, only when *ChainShot app* is selected |

No host permission: the extension cannot read web pages in the background.

The `key` in `manifest.json` fixes the extension ID (`dcccfnbiglhpbcdhbmokfilfgpfnmikf`) so that the ChainShot app
recognises it; store packages are built without it.

Firefox: no MHTML (no `pageCapture` API in Firefox); everything else works, including the browser link.
