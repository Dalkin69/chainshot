# chainshot.py

Reference implementation of the [ChainShot](https://github.com/Dalkin69/chainshot) journal:
a single file, Python 3.9+, standard library only, no network access except the RFC 3161
timestamps you ask for.

```bash
pipx install "git+https://github.com/Dalkin69/chainshot#subdirectory=python"   # or copy chainshot.py
chainshot init ./case --case "DEMO-01" --examiner "Your name"
chainshot add ./case evidence.E01 --tag P00
chainshot verify ./case
chainshot seal ./case --tsa
chainshot report ./case
chainshot package ./case          # zip for delivery: case + chainshot.py + VERIFY.txt + SHA256SUMS.txt
```

Browser extension on Linux or macOS (captures saved into the current case):

```bash
chainshot register-browser        # Chrome, Chromium, Edge, Brave, Vivaldi profiles found
chainshot use ./case              # current case, shared with ChainShot.exe and the PowerShell module
chainshot tag P17
```

See the [main README](../README.md), the [auditor guide](../docs/verification.md) and the
[browser link](../docs/browser-link.md).
Tests: `python -m unittest discover -s tests -v` (from this folder).
