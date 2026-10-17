# Code signing (SignPath Foundation)

`ChainShot.exe` is not signed yet, so Windows SmartScreen asks for a confirmation on first launch.
[SignPath Foundation](https://signpath.org) gives free code signing certificates to open source projects.
This page lists what the project already does to meet their [conditions](https://signpath.org/terms) and
what the maintainer has to do.

## Conditions and where they are met

| Condition | Status |
| --- | --- |
| OSI-approved license, no proprietary component | MIT; the app uses only .NET Framework (system library) |
| Released, actively maintained | releases on GitHub, CI on every change |
| Functionality documented on the download page | [website](https://dalkin69.github.io/chainshot/) and README |
| Own project, own binaries | `ChainShot.exe` is built from this repository by the release workflow |
| No hacking tools | ChainShot documents what the examiner sees; it does not look for or exploit vulnerabilities |
| Privacy policy, no hidden data transfer | [PRIVACY.md](../PRIVACY.md): no telemetry; the only request is the RFC 3161 timestamp the user asks for |
| System changes only with the user's consent | Start with Windows and Start menu are options; the browser link is asked at first start |
| Uninstallation | *Options > Uninstall from this computer*, `ChainShot.exe uninstall`, `Uninstall-ChainShot.ps1`, Scoop runs it too |
| Code signing policy on the home page | [code-signing.html](https://dalkin69.github.io/chainshot/code-signing.html), linked from the footer |
| Consistent product metadata | `Product` = ChainShot and the same version everywhere (`tests/check_versions.py`) |
| MFA for every team member | **to do**: enable two-factor authentication on GitHub (and on SignPath once the account exists) |

## Steps for the maintainer

1. Enable GitHub Pages: *Settings > Pages > Source: GitHub Actions*. The site is published at
   `https://dalkin69.github.io/chainshot/` by `.github/workflows/pages.yml`. A custom domain is optional.
2. Make the project findable: add the website to the repository *About* box, register it in
   [Google Search Console](https://search.google.com/search-console) (verify the property, submit `sitemap.xml`),
   and link it from the store listings. A search for "ChainShot forensic" should then lead to it.
3. Enable two-factor authentication on GitHub.
4. Apply at [signpath.org/apply](https://signpath.org/apply) with: project name *ChainShot*, the repository URL, the
   website, the code signing policy URL, the release asset to sign (`chainshot-app-<version>.zip` > `ChainShot.exe`).
5. Once accepted, SignPath gives an organisation ID and a project. Then (to be done in a pull request):
   - add the secret `SIGNPATH_API_TOKEN` and the variable `SIGNPATH_ORGANIZATION_ID` to the repository;
   - in the release workflow, upload the unsigned `ChainShot.exe` as an artifact and submit it with
     [`signpath/github-action-submit-signing-request`](https://github.com/SignPath/github-action-submit-signing-request),
     wait for the signed file and package that one;
   - change the status on `site/code-signing.html` and keep the statement
     *Free code signing provided by SignPath.io, certificate by SignPath Foundation.*
   Every signing request is then approved by hand in SignPath, as their rules require.
