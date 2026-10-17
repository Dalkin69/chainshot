# Contributing

Thank you for helping. Issues and pull requests are welcome, in English or French.

## Ground rules

- **The journal format is a contract.** A change to `docs/journal-format.md` must update, in the
  same pull request, the four implementations (`python/chainshot.py`, `app/src/Journal.cs`,
  `windows/ChainShot/ChainShot.psm1`, `extension/lib/chain.js`) and regenerate the vectors:
  `python tests/vectors/make_vectors.py`. CI fails if the vectors are out of date.
- **Old journals must keep verifying.** Never change how an existing format version is hashed;
  introduce a new version instead.
- **PowerShell files stay pure ASCII** (Windows PowerShell 5.1 reads BOM-less files with the
  ANSI code page) and must work on 5.1 and 7.
- **`chainshot.py` stays standard-library only**: auditors must be able to run it anywhere.
- **`ChainShot.exe` stays a single file**: C# 7.3, .NET Framework 4.8 (built into Windows 10 and 11),
  no NuGet package at run time. It builds with `dotnet build app/ChainShot.csproj` on Windows.
- **Versions move together**: `python tests/check_versions.py` must pass.
- Every new behaviour comes with a test.

## Running the tests

```bash
python -m unittest discover -s python/tests -v
pwsh -c "Invoke-Pester ./windows/tests"            # Pester 5 (4.10+ also works)
npm install && npx playwright install chromium && npm run test:extension
# Firefox (needs Firefox 140+, geckodriver, pip install selenium):
python tools/build_extension.py --out dist && python tests/firefox_e2e.py dist/extension-firefox
# Windows only:
dotnet build app/ChainShot.csproj -c Release
python tests/app_e2e.py app/bin/Release/ChainShot.exe
```

The Windows capture and tray tests only run on Windows; they are skipped elsewhere and run in CI.
Every CI run also publishes the `ChainShot.exe` it built and tested as the `chainshot-app-exe` artifact.

## Regenerating the README illustrations

```bash
node docs/make-screenshots.mjs
```

`docs/img/app-window.png` comes from the app job of the CI (`test-output/app-demo.png`).
