# Threat model

## What this project does and where untrusted input enters

ChainShot records screenshots, files and notes as forensic evidence: each item is hashed (SHA-256) and
written to an append-only journal (`journal.jsonl`) whose entries are chained by hash; seals freeze the
journal and can carry an RFC 3161 timestamp. Its value is that **anyone can later check that nothing was
edited, removed, reordered or re-dated**. The same journal format (`docs/journal-format.md`, shared vectors in
`tests/vectors/`) is implemented four times:

| Component | Path | Runs on |
| --- | --- | --- |
| `chainshot.py`: reference implementation and independent verifier | `python/chainshot.py` | any OS, Python 3.9+, no dependency |
| `ChainShot.exe`: Windows app and command line (C# 7.3, .NET Framework 4.8) | `app/src/` | Windows (builds here, does not run) |
| PowerShell module and tray | `windows/ChainShot/` | Windows PowerShell 5.1, PowerShell 7 |
| Browser extension (Manifest V3, Chrome/Edge/Firefox) | `extension/` | browser |

Untrusted input, by priority:

1. **Case folders received from someone else**, given to `verify`, `report`, `package` (any implementation):
   `journal.jsonl`, `case.json`, `seals/*.json`, `seals/*.tsr`, the evidence files and the paths written in
   the journal. A recipient (client, court expert, opposing party) verifies a case they did not produce.
2. **Native messaging** (`ChainShot.exe` started by the browser, `chainshot.py native-host`): length-prefixed
   JSON on stdin from the extension (`docs/browser-link.md`). The browser restricts which extension may
   connect, but the payload carries data from arbitrary web pages: URL, title, PNG and MHTML bytes.
3. **Web pages** seen by the extension: title and URL end up in the banner drawn on the image, in the
   journal and in the HTML report; full-page capture injects a script into the captured page.
4. **RFC 3161 responses** from the time-stamp authority (network, HTTP): ASN.1/DER parsed by our own code
   in Python, C# and PowerShell.

## Components that matter most / least

- Most: verification logic (canonical form, hash chain, seal and timestamp checks) in all four
  implementations, and the DER/CMS parsing of timestamp tokens; native host message handling and how it
  builds file names inside the case; the HTML report generator (escaping of journal content); the custom
  JSON reader/writer of the C# app (`app/src/MiniJson.cs`).
- In scope but lower: the PowerShell tray and GUI code, the extension popup.
- Out of scope: `site/` (static website), `docs/`, `tools/`, `packaging/`, `tests/`, `.github/`, and
  anything that requires the attacker to already control the examiner's Windows account.

## How to exercise it

- `python -m unittest discover -s python/tests` (includes a local OpenSSL TSA for RFC 3161).
- `chainshot verify <case>`, `chainshot report <case>`, `chainshot seal <case> --tsa <url>`,
  `chainshot native-host` (reads native messaging frames on stdin, see `docs/browser-link.md`).
- Shared vectors: `tests/vectors/canonical-vectors.json`; generator `tests/vectors/make_vectors.py`.
- PowerShell: `pwsh -c "Import-Module ./windows/ChainShot/ChainShot.psd1; Test-ChainShotCase -CasePath <case>"`;
  Pester tests in `windows/tests` (capture and tray tests skip on Linux).
- `ChainShot.exe` is built to `app/bin/Release/ChainShot.exe` but needs Windows to run; review `app/src`.

## How you rate severity

- **Critical**: a verifier reports a case as intact (`ALL EVIDENCE INTACT`, exit 0) although an entry, a
  file, a seal or a timestamp was altered, removed, reordered or forged; two implementations disagree on
  the same journal in a way an attacker can choose; code execution from verifying or reporting a case,
  or from a native message; writing outside the case folder.
- **High**: an RFC 3161 token accepted for the wrong imprint or with an invalid signature; script
  execution in the HTML report (stored XSS from a page title, URL or note); reading files outside the
  case folder through journal paths; a crash or hang of the native host triggered by a web page.
- **Medium**: denial of service from a crafted case or message (memory, CPU), misleading but detectable
  output, information leaks to the time-stamp authority beyond the seal hash.
- **Low**: issues that need the attacker to already control the examiner's account or settings files.

## Anything to leave alone

- SHA-256 and the RFC 3161 trust model are by design; do not report "SHA-256 could be replaced".
- The time-stamp authority is chosen by the user (default FreeTSA); its availability is not our concern.
- Unsigned `ChainShot.exe` and the absence of an installer are known (code signing is pending).
- Screenshots may contain personal data: that is the purpose of the tool, not a finding.
