# SPDX-License-Identifier: MIT
# Build environment for Anthropic's OSS Scanner (https://github.com/anthropics/oss-scanner).
# The scanner builds this image with the repository as context, then audits it without network access,
# so everything the tests need is installed here.
#
#   docker build -f .oss-scanner/Dockerfile -t chainshot-scan .
FROM debian:bookworm
ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    POWERSHELL_TELEMETRY_OPTOUT=1 \
    PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1

RUN apt-get update && apt-get install -y --no-install-recommends \
      ca-certificates curl git python3 python3-venv openssl nodejs \
    && rm -rf /var/lib/apt/lists/*

# PowerShell 7 (module and Pester tests) and the .NET SDK (compiles ChainShot.exe against the
# .NET Framework 4.8 reference assemblies; the program itself only runs on Windows).
RUN curl -fsSL -o /tmp/ms.deb https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb \
    && dpkg -i /tmp/ms.deb && rm /tmp/ms.deb \
    && apt-get update && apt-get install -y --no-install-recommends powershell dotnet-sdk-8.0 \
    && rm -rf /var/lib/apt/lists/*
RUN pwsh -NoProfile -Command "Set-PSRepository PSGallery -InstallationPolicy Trusted; Install-Module Pester -MinimumVersion 5.5.0 -Scope AllUsers -Force"

# scanner contract: the checkout lives inside the image, at /src
COPY . /src
WORKDIR /src

# chainshot.py (reference implementation and verifier) as an installed command.
RUN python3 -m venv /opt/venv && /opt/venv/bin/pip install --no-cache-dir ./python
ENV PATH=/opt/venv/bin:$PATH

# ChainShot.exe (restore needs the network, so it happens here).
RUN dotnet build app/ChainShot.csproj -c Release -nologo

# Cheap tests: they must not stop the build.
RUN (python -m unittest discover -s python/tests 2>&1 | tail -5) || echo "WARNING: Python tests reported failures"
RUN python tests/check_versions.py || echo "WARNING: version check failed"
RUN pwsh -NoProfile -Command "Import-Module Pester; \$c = New-PesterConfiguration; \$c.Run.Path = 'windows/tests'; \$c.Output.Verbosity = 'None'; \$c.Run.PassThru = \$true; \$r = Invoke-Pester -Configuration \$c; 'Pester: {0} passed, {1} failed, {2} skipped' -f \$r.PassedCount, \$r.FailedCount, \$r.SkippedCount" \
    || echo "WARNING: Pester reported failures"
