# Security

## Reporting a vulnerability

Please report security problems privately through GitHub's
[private vulnerability reporting](https://github.com/Dalkin69/chainshot/security/advisories/new),
not in a public issue. Describe the affected component, the steps to reproduce and the impact.

## Threat model

ChainShot makes evidence **tamper-evident**: it lets a third party detect changes. It does not
make tampering impossible, and it does not protect the examiner's computer.

| Threat | Covered | How |
| --- | --- | --- |
| A capture is edited, cropped or replaced after it was taken | yes | SHA-256 of each file in the journal |
| An entry's time, tag or note is edited | yes | chained canonical text of every entry |
| An entry is removed, inserted or reordered | yes | hash chain and sequence numbers |
| The whole journal is rebuilt with a consistent chain | yes, **if** the seal hash is kept outside the folder | seal + `journal_prefix_sha256`, seal SHA-256 in the signed report |
| The examiner's clock is wrong or changed | partly | RFC 3161 seals give an independent time for the sealed entries |
| What is displayed on screen is false (compromised system, forged web page) | no | ChainShot records what was seen, not whether it is true |
| The examiner's computer is compromised before capture | no | use a trusted analysis workstation |
| Someone with access to the folder deletes it | no | keep copies, follow your chain-of-custody procedure |

## Design choices

- **SHA-256** for files, entries and chain; MD5 is recorded only as an informative extra
  (some forensic tools still display it).
- **No network access** except the optional RFC 3161 request, sent to the authority you choose.
- **No dependency** in the verifier, so it can be audited and run offline.
- The browser extension asks only for `activeTab`, `downloads` and `storage`: it cannot read
  pages in the background and can capture a tab only after a click or its keyboard shortcut.
