# Privacy

Also on the website: [English](https://dalkin69.github.io/chainshot/privacy.html), [français](https://dalkin69.github.io/chainshot/fr/privacy.html).

ChainShot is a forensic tool: what it records belongs to you and to your case, and nobody else.
This page applies to every component: the Windows app (`ChainShot.exe`), the PowerShell module,
`chainshot.py` and the browser extension. It is also the privacy policy of the extension in browser stores.

## In short

- **No telemetry, no analytics, no crash reports, no account, no advertising.**
- **No automatic network connection.** ChainShot does not check for updates and does not "phone home".
- **Everything stays on your computer**, in the case folders you choose.
- **Open source** (MIT license): every line can be read and audited in this repository, and the
  release files are built from it by the public GitHub Actions workflow, with their SHA-256 in `SHA256SUMS.txt`.

## The only network connection: RFC 3161 timestamps, when you ask for one

When you choose **Seal + timestamp** (app), `seal --tsa` (command line) or *Seal it with an RFC 3161
timestamp* before a package, ChainShot sends one request to the time-stamp authority you configured
(default: [FreeTSA](https://freetsa.org)). The request contains:

- the **SHA-256 of the seal file** (32 bytes: a fingerprint, from which nothing can be read back);
- a random number (nonce) and the hash algorithm identifier.

No capture, file, note, case name or personal data is sent. The authority returns a signed token
(`seals/seal-<UTC>.tsr`) that is stored in the case. Choose an authority your organisation trusts
(Options > Timestamp authority, or `--tsa <url>`).

Links in the Help menu open this repository in your web browser; nothing is sent by ChainShot itself.

## Browser extension

| | |
| --- | --- |
| Data collected | None. The extension has no server and sends nothing over the network. |
| What it captures | The visible part of the active tab, or the whole page (it is scrolled and stitched), **only** when you click *Capture tab* or press its shortcut, with the page URL and title; an MHTML copy of the page if you enable *Page archive*. |
| Where it goes | Your Downloads folder (`ChainShot/<case>/`), or, if you choose *ChainShot app*, the case open in ChainShot on the same computer (through Chrome native messaging, a local channel; nothing leaves the computer). |
| Stored in the browser | The journal of the cases created in the browser, the tag and the next note (`chrome.storage.local`, on this computer only). |
| Permissions | `activeTab` (capture the tab you are on, after your click), `scripting` (full page: scroll the page you captured and restore it), `downloads` (write the case files), `storage` (remember the journal and settings), `nativeMessaging` (talk to the ChainShot app on this computer, only if you select it), optional `pageCapture` (MHTML archive, Chromium, asked when you enable it). No host permission: the extension cannot read websites by itself. In Firefox the extension declares that it collects no data. |

## Windows app and PowerShell module

- Settings are stored in `%APPDATA%\ChainShot` (`state.json`: current case and tag; `gui.json`: app preferences;
  `com.chainshot.host.json`: browser link). Delete the folder to reset ChainShot.
- The app writes these registry values, for the current user only: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\ChainShot`
  when you enable *Start with Windows*, and `HKCU\Software\<browser>\NativeMessagingHosts\com.chainshot.host` for the
  browser link (asked at first start; Options > Accept captures from the browser extension), plus
  `HKCU\Software\Mozilla\NativeMessagingHosts\com.chainshot.host` for Firefox. *Options > Uninstall from this computer*
  (or `ChainShot.exe uninstall`) removes all of them.
- Captures contain what is on your screen: like any screenshot, they may include personal data. Store case folders
  on media you control and apply your organisation's rules on evidence and personal data.

## Contact

Questions or concerns: open an issue on [GitHub](https://github.com/Dalkin69/chainshot/issues).
Security problems: see [SECURITY.md](SECURITY.md).
