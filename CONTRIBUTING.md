# Contributing

Thank you for helping. Issues and pull requests are welcome, in English or French.

## Ground rules

- **The journal format is a contract.** A change to `docs/journal-format.md` must update, in the
  same pull request, the three implementations (`python/chainshot.py`,
  `windows/ChainShot/ChainShot.psm1`, `extension/lib/chain.js`) and regenerate the vectors:
  `python tests/vectors/make_vectors.py`. CI fails if the vectors are out of date.
- **Old journals must keep verifying.** Never change how an existing format version is hashed;
  introduce a new version instead.
- **PowerShell files stay pure ASCII** (Windows PowerShell 5.1 reads BOM-less files with the
  ANSI code page) and must work on 5.1 and 7.
- **`chainshot.py` stays standard-library only**: auditors must be able to run it anywhere.
- Every new behaviour comes with a test.

## Running the tests

```bash
python -m unittest discover -s python/tests -v
pwsh -c "Invoke-Pester ./windows/tests"            # Pester 5 (4.10+ also works)
npm install && npx playwright install chromium && npm run test:extension
```

The Windows capture and tray tests only run on Windows; they are skipped elsewhere and run in CI.

## Regenerating the README illustrations

```bash
node docs/make-screenshots.mjs
```
