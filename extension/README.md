# ChainShot browser extension

Manifest V3 extension for Chromium browsers (Chrome, Edge, Brave; Chromium 116+).

1. `chrome://extensions` (or `edge://extensions`) > enable **Developer mode** > **Load unpacked** > select this folder.
2. Click the ChainShot icon, create a case, capture with the button or `Alt+Shift+S`
   (change it at `chrome://extensions/shortcuts`).

Each case is written to `Downloads/ChainShot/<case>-<UTC>/` and verified with
`python chainshot.py verify <folder>`. The journal is also kept in the extension's storage:
**Rewrite journal file** writes it again if the file was moved or deleted.

Permissions: `activeTab` (capture only after a click or the shortcut), `downloads` (write the case
files), `storage` (keep the journal). No host permission, no network access.

Firefox is not supported yet (planned).
