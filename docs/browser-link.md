# Browser link: extension captures in the app's case

By default the browser extension keeps its own cases in the Downloads folder. With the browser link,
it saves its captures **into the case open in ChainShot** instead, next to the screen captures, files
and notes of that case, in one journal.

```
 Chrome / Edge / Brave                         same computer
┌──────────────────────┐   native messaging   ┌──────────────────────────────┐
│ ChainShot extension  │ ───────────────────► │ ChainShot.exe (Windows)      │
│ capture + banner     │  JSON on stdin/out   │ or chainshot.py native-host  │
└──────────────────────┘ ◄─────────────────── │ hashes, names, chains, saves │
                                              └──────────────┬───────────────┘
                                                             ▼
                                              <current case>/captures/0012_..._P17.png
                                              <current case>/journal.jsonl  (type "browser")
```

Nothing goes over the network: the browser starts the host program on the same computer and talks
to it through its standard input and output ([Chrome native messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging)).

## Set up

| System | Host | Registration |
| --- | --- | --- |
| Windows | `ChainShot.exe` | the app asks at its first start (then Options > *Accept captures from the browser extension*), or `ChainShot.exe register-browser` |
| Linux, macOS (or Windows without the app) | `chainshot.py` | `python3 chainshot.py register-browser`, then `python3 chainshot.py use <case folder>` |

Then, in the extension popup, choose **ChainShot app**. The popup shows the current case of the app; captures,
notes, the tag and *Verify chain* now work on that case. Choose **This browser** to go back to the
Downloads cases.

Registration writes a small manifest (`com.chainshot.host.json`) that names the host program and the
only extension allowed to start it, and records its location:

- Windows: `HKCU\Software\{Google\Chrome, Microsoft\Edge, BraveSoftware\Brave-Browser, Chromium, Vivaldi}\NativeMessagingHosts\com.chainshot.host`;
- Linux: `~/.config/{google-chrome, chromium, microsoft-edge, BraveSoftware/Brave-Browser, vivaldi}/NativeMessagingHosts/`;
- macOS: `~/Library/Application Support/{Google/Chrome, Chromium, Microsoft Edge, BraveSoftware/Brave-Browser, Vivaldi}/NativeMessagingHosts/`.

Firefox reads its own manifest format (`allowed_extensions` with the add-on ID `chainshot@dalkin69`):
`HKCU\Software\Mozilla\NativeMessagingHosts\com.chainshot.host` on Windows, `~/.mozilla/native-messaging-hosts/` on
Linux, `~/Library/Application Support/Mozilla/NativeMessagingHosts/` on macOS. Firefox starts the host with the manifest
path and the add-on ID as arguments; both hosts recognise them.

`--remove` (or unchecking the option in the app, or `ChainShot.exe uninstall`) deletes them. If ChainShot.exe is moved, the app updates the
registration at its next start.

**Extension IDs.** The `key` in `extension/manifest.json` gives the unpacked extension a fixed ID,
`dcccfnbiglhpbcdhbmokfilfgpfnmikf`, which the host allows. Store versions get their own ID: they are added to
`BrowserLink.ExtensionIds` (app) and `EXTENSION_IDS` (`chainshot.py`) when published; meanwhile
`register-browser --extension-id <id>` allows another one.

## Who does what

- The **extension** captures the visible tab (after your click or shortcut) and draws the banner below it, with
  the entry number given by the host: `#12 (capture 5) | case | tag | UTC | examiner @ computer`, then the URL,
  the title and the note.
- The **host** checks that the image is a PNG, names it `captures/NNNN_<UTC>_<tag>.png`, hashes it and appends a
  `browser` entry to the journal of the current case, under the same lock as the other ChainShot tools. The entry
  keeps the extension as `source` and records the host in `extra.via`.
- If another tool added an entry between the two steps, the host answers `retry` with the new number and the
  extension redraws the banner (at most three times), so the number on the image always matches the journal.
- The capture time is the browser's, when it is within 10 minutes of the host's clock; otherwise the host's.

## Protocol (version 2)

Every message is UTF-8 JSON preceded by its length (32-bit, native byte order), as specified by Chrome.
Each request gets one reply; `"ok": false` replies carry an `error` text.

| Request | Reply |
| --- | --- |
| `{"type":"hello"}` | `{"ok":true,"protocol":1,"tool":"chainshot-app/1.2.0","host":"PC-042","tag":"P17","case":{"name","path","examiner","entries","next_seq","next_capture"}}`; `case` is `null` (with a `warning`) when no case is selected |
| `{"type":"set_tag","tag":"P18"}` | `{"ok":true,"tag":"P18"}`: the tag shared with the app and the module |
| `{"type":"capture","png_base64","utc","expected_seq","url","title","tag","note","source","extra":{"viewport","user_agent"}}` | `{"ok":true,"entry":{"seq","utc","file","sha256","chain"}}`, or `{"ok":false,"retry":true,"case":{...}}` when `expected_seq` is no longer the next entry |
| `{"type":"note","text","source"[,"tag"]}` | `{"ok":true,"entry":{"seq","utc","chain"}}`; without `tag`, the current tag |
| `{"type":"capture","format":"mhtml","data_base64",...}` (protocol 2) | page archive saved as `captures/NNNN_<UTC>_<tag>.mhtml`, entry type `browser`; no banner, so no `expected_seq` |
| `{"type":"verify"}` | `{"ok":true,"intact":true,"entries":12,"seals":1,"problems":[]}` (files re-hashed) |

Limits: PNG or MHTML, 60 MB per message. Only the fields listed in `extra` are kept.

## Tests

- `python/tests/test_v12.py` and `tests/app_e2e.py` drive both hosts with framed messages (Python on Linux,
  macOS and Windows; `ChainShot.exe` on Windows), including stale numbers, invalid images and registration.
- `tests/extension.test.mjs` runs the real extension in Chromium with `chainshot.py` registered as its host:
  captures, notes, tag and verification go into a case that `chainshot.py verify` then checks.
