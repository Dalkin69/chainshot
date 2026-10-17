# Publishing the browser extension

The extension works today as an *unpacked* extension (developer mode). Publishing it in the stores
removes the developer-mode warning, gives automatic updates and a one-click install. Everything
needed is in this repository; the steps below need the maintainer's own accounts.

| Store | Browsers | Account | Cost |
| --- | --- | --- | --- |
| [Chrome Web Store](https://chrome.google.com/webstore/devconsole) | Chrome, Brave, Vivaldi, Opera | Google account + developer registration | one-time registration fee (5 USD at the time of writing) |
| [Microsoft Edge Add-ons](https://partner.microsoft.com/dashboard/microsoftedge) | Edge | Microsoft Partner Center (individual developer) | free |
| [Firefox Add-ons (AMO)](https://addons.mozilla.org/developers/) | Firefox 140+ | Mozilla account | free |

## 1. Packages

Each GitHub release attaches the packages built by `tools/build_extension.py`:

| File | Upload to |
| --- | --- |
| `chainshot-extension-store-<version>.zip` | Chrome Web Store and Edge Add-ons (manifest without `key`: stores assign their own ID) |
| `chainshot-extension-firefox-<version>.zip` | addons.mozilla.org (background script, add-on ID `chainshot@dalkin69`, Firefox 140+, data collection: none) |
| `chainshot-extension-<version>.zip` | not for stores: unpacked installation (developer mode) |

To build them locally: `python tools/build_extension.py --out dist --zip v1.3.0`.

## 2. Listing

| Field | Value |
| --- | --- |
| Name | ChainShot - forensic web captures |
| Summary (132 chars max) | Timestamped, hash-chained screenshots of web pages (URL, title, UTC time, SHA-256) for forensic and investigation reports. |
| Category | Developer Tools (Chrome) / Productivity (Edge) |
| Language | English |
| Icon | `extension/icons/icon128.png` |
| Screenshots (1280x800) | `docs/store/store-1-capture.png`, `store-2-verify.png`, `store-3-app.png` |
| Small promo tile (440x280) | `docs/store/promo-small-440x280.png` |
| Homepage / support | https://github.com/Dalkin69/chainshot - https://github.com/Dalkin69/chainshot/issues |
| Privacy policy | https://github.com/Dalkin69/chainshot/blob/main/PRIVACY.md |

Description:

```text
ChainShot turns screenshots of web pages into evidence that anyone can check later.

• One click (or Alt+Shift+S) captures the visible part of the tab, or the whole page (scrolled and stitched).
• Optional page archive (MHTML) saved and hashed next to the screenshot.
• A banner is added BELOW the page, never over it: entry number, case, tag, UTC time, examiner, URL and title.
• Each capture is hashed with SHA-256 and chained to the previous entry in an append-only journal:
  editing, removing or reordering anything breaks the chain.
• Cases are plain folders in your Downloads folder, or, with the free ChainShot app for Windows,
  the case you are already working on, next to your screen captures, files and notes.
• Verification needs nothing but Python: chainshot.py verify <case>. Seals and RFC 3161 timestamps
  are available in the app and in chainshot.py.

No account. No telemetry. No server: nothing leaves your computer.
Open source (MIT): https://github.com/Dalkin69/chainshot

Made for digital forensics coursework, investigations, OSINT, compliance and anyone who must prove
what a web page showed at a given time.
```

## 3. Privacy practices (Chrome) / Properties (Edge)

**Single purpose**: *Capture the visible web page as a timestamped, hashed evidence file and record it in a
tamper-evident journal.*

| Permission | Justification |
| --- | --- |
| `activeTab` | Captures the tab the user is on, only after the user clicks the button or presses the shortcut. |
| `scripting` | Full page capture: after the user's click, scrolls the page part by part, hides fixed headers while capturing and restores the page. Nothing is read or sent. |
| `pageCapture` (optional) | Page archive: saves the page as MHTML next to the screenshot, only if the user enables it in the popup (asked at that moment). |
| `downloads` | Writes the capture, the journal and the case metadata into the user's Downloads folder. |
| `storage` | Keeps the journal of the cases created in the browser and the user's tag and note on this computer. |
| `nativeMessaging` | Optional link with the ChainShot desktop app on the same computer, so captures are saved into the case open in the app. Only used when the user selects "ChainShot app". |

Remote code: **No**. Data usage: tick **none** of the data categories (the extension does not collect or
transmit user data), then certify the three statements (no sale, no unrelated use, no creditworthiness).

## 4. Firefox (addons.mozilla.org)

1. [Developer Hub](https://addons.mozilla.org/developers/) > *Submit a New Add-on* > *On this site*.
2. Upload `chainshot-extension-firefox-<version>.zip`. The automatic validation is the same linter as
   the CI (`web-ext lint`).
3. *Do you use a code generator, minifier...?*: **No**. The only build step inlines `lib/chain.js` into
   `background.js`; if the reviewer asks for sources, give the release tag and `python tools/build_extension.py`.
4. Listing: same description, screenshots and privacy policy as above. Category: *Privacy & Security*
   or *Other*. License: MIT.
5. Data collection: the manifest already declares **none** (Firefox shows it at install).

Firefox limits: no MHTML page archive (Firefox has no `pageCapture` API); full page capture and the
browser link work. In the Downloads mode Firefox may open its download panel for each file: use the
*ChainShot app* destination, or turn off *Always ask you where to save files*.

## 5. After the first publication

1. Note the extension ID shown by each store (32 letters; Firefox keeps `chainshot@dalkin69`).
2. Add both IDs to `BrowserLink.ExtensionIds` in `app/src/BrowserLink.cs` and to `EXTENSION_IDS` in
   `python/chainshot.py`, so the ChainShot app accepts the store versions; release a new version.
   Until then, users can run `ChainShot.exe register-browser --extension-id <id>`.
3. Replace the "Load unpacked" instructions in the READMEs with the store links.
4. For each new version: raise the version everywhere (`python tests/check_versions.py`), publish the GitHub
   release, then upload its `chainshot-extension-store-<version>.zip` to both stores. Reviews usually take a
   few days.
