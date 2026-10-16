# chainshot.py

Reference implementation of the [ChainShot](https://github.com/Dalkin69/chainshot) journal:
a single file, Python 3.9+, standard library only.

```bash
pipx install "git+https://github.com/Dalkin69/chainshot#subdirectory=python"   # or copy chainshot.py
chainshot init ./case --case "DEMO-01" --examiner "Your name"
chainshot add ./case evidence.E01 --tag P00
chainshot verify ./case
chainshot seal ./case --tsa
chainshot report ./case
```

See the [main README](../README.md) and the [auditor guide](../docs/verification.md).
Tests: `python -m unittest discover -s tests -v` (from this folder).
