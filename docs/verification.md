# Verifying a ChainShot case

This guide is for the person who **receives** evidence: a client, an auditor, a court expert,
a teacher. You do not need the tools that took the captures, only Python 3.9+ (and OpenSSL
for the optional RFC 3161 signature check).

## What you should have received

1. The **case folder** (`chainshot-case.json`, `journal.jsonl`, `captures/`, `seals/`...).
2. A **signed report** (or an email sent at delivery) stating the **SHA-256 of a seal**.
   This value, kept outside the folder, is what anchors everything else.

## 1. Get the verifier from a source you trust

Download `chainshot.py` from the project's releases, not from the case folder. It is a single
file using only the Python standard library: you can read it before running it.

## 2. Verify the folder

```bash
python chainshot.py verify path/to/case
```

Each entry must be `OK`, and the last line must read `RESULT: ALL EVIDENCE INTACT`
(exit code 0). Possible problems:

| Message | Meaning |
| --- | --- |
| `FILE HASH MISMATCH (file modified)` | the file differs from the one recorded (edited, re-saved, cropped) |
| `FILE MISSING` | the file is not where the journal says |
| `CHAIN BROKEN (entry edited, removed or reordered)` | the journal itself was changed: time, tag, note, order, or a line removed |
| `last_chain does not match the journal` / `journal prefix hash differs` | the journal is not the one that was sealed |
| `timestamp does not certify this seal` | the `.tsr` belongs to another seal, or the seal was changed |

A `WARNING` about a timestamp earlier than the previous entry usually means the examiner's
clock was changed during the work: ask about it, it is not proof of tampering by itself.

## 3. Compare the seal with the value outside the folder

```bash
# Linux / macOS
sha256sum path/to/case/seals/seal-*.json
# Windows PowerShell
Get-FileHash path\to\case\seals\seal-*.json -Algorithm SHA256
```

One of the seals must have exactly the SHA-256 given in the signed report. If none does, the
folder is not the one that was delivered, even if `verify` says everything is consistent:
someone able to rewrite the whole folder can build a new, internally consistent chain.

## 4. Check the RFC 3161 timestamp (if present)

`verify` always checks that the timestamp token certifies the seal's hash and shows the
authority's time. To also check the authority's signature, provide its CA certificate:

```bash
python chainshot.py verify path/to/case --tsa-ca tsa-cacert.pem
python chainshot.py tsr-info path/to/case/seals/seal-XXXX.tsr
```

For FreeTSA (the default authority), the CA certificate is published at
`https://freetsa.org/files/cacert.pem`. Equivalent manual check:

```bash
openssl ts -verify -in seal-XXXX.tsr -data seal-XXXX.json -CAfile tsa-cacert.pem
```

A valid timestamp proves that the seal, and therefore every entry it covers, existed at the
authority's time, whatever the examiner's clock said.

## 5. Optional: an HTML view

```bash
python chainshot.py report path/to/case -o /tmp/case-report.html
```

The report re-runs the verification and shows thumbnails, hashes and seal status. It is a
convenience view; the journal and the files are the evidence.
