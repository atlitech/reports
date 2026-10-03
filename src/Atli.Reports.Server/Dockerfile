# Builds the Atli Reports Server as a NativeAOT binary and runs it next to chrome-headless-shell.
#
# The build context is the repository root:
#   docker build -f src/Atli.Reports.Server/Dockerfile -t atli-reports-server .
# or, from this directory:
#   docker compose up --build
#
# Build arguments:
#   CHROME_VERSION  The chrome-headless-shell version from Chrome for Testing. Defaults to the version
#                   pinned in the browser stage below, which a scheduled workflow
#                   (.github/workflows/chrome-headless-shell-bump.yml) proposes updating when Chrome
#                   for Testing ships a newer stable release. `stable` takes the current stable
#                   release instead; add --no-cache-filter browser so Docker downloads it again
#                   rather than reusing an earlier download.
#
# The base images are pinned by the digest of their multi-arch index; Dependabot updates the tag and
# digest together (.github/dependabot.yml).

# Stage 1: build (NativeAOT). The -aot SDK image carries the native toolchain NativeAOT links with
# (clang, zlib). The build and runtime stages share Ubuntu 24.04, so the binary runs against the
# glibc it was linked with.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot@sha256:96f3b7d45f53eb05990f05b89ce61c4e23d07a5098521c2f20b018630e34f298 AS build
WORKDIR /src
# Restore from the project files alone, so a source change reuses the restored packages (the
# NativeAOT compiler among them). The compiler and runtime packs are per architecture, so restore
# and publish both target the build's own runtime: the stage runs on the image's architecture.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Atli.Reports.Engine/Atli.Reports.Engine.csproj src/Atli.Reports.Engine/
COPY src/Atli.Reports.Worker.Protocol/Atli.Reports.Worker.Protocol.csproj src/Atli.Reports.Worker.Protocol/
COPY src/Atli.Reports.Server/Atli.Reports.Server.csproj src/Atli.Reports.Server/
RUN dotnet restore src/Atli.Reports.Server --use-current-runtime
COPY src/Atli.Reports.Engine/ src/Atli.Reports.Engine/
COPY src/Atli.Reports.Worker.Protocol/ src/Atli.Reports.Worker.Protocol/
COPY src/Atli.Reports.Server/ src/Atli.Reports.Server/
RUN dotnet publish src/Atli.Reports.Server -c Release --use-current-runtime --no-restore -o /app
# The native binary's debug symbols (.dbg, about 40 MB), the engine's symbols and XML docs: none
# is loaded at runtime. The binary, appsettings.json, and the static assets manifest stay.
RUN rm -f /app/*.dbg /app/*.pdb /app/*.xml

# Stage 2: download chrome-headless-shell, Chromium's dedicated headless build. Compared with the full
# browser it starts faster, prints faster, and creates the per-conversion browser contexts the engine
# isolates documents with several times faster.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble@sha256:099f6f87ed745377dd27bd722f0d1a352bca71b4fddaabfd75e7c064bcaa82da AS browser
ARG TARGETARCH
# The pinned chrome-headless-shell version: the single source of truth for the image's browser.
# .github/scripts/chrome-headless-shell.sh reads and rewrites this line, so keep it a plain
# ARG CHROME_VERSION=<version>.
ARG CHROME_VERSION=154.0.8037.92
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
    unzip \
    && rm -rf /var/lib/apt/lists/*
RUN set -eux; \
    case "${TARGETARCH:-amd64}" in \
      amd64) platform=linux64 ;; \
      arm64) platform=linux-arm64 ;; \
      *) echo "Unsupported architecture: ${TARGETARCH}" >&2; exit 1 ;; \
    esac; \
    version="${CHROME_VERSION}"; \
    if [ "${version}" = "stable" ]; then \
      version="$(curl -fsSL https://googlechromelabs.github.io/chrome-for-testing/LATEST_RELEASE_STABLE)"; \
    fi; \
    curl -fsSLo /tmp/chrome-headless-shell.zip \
      "https://storage.googleapis.com/chrome-for-testing-public/${version}/${platform}/chrome-headless-shell-${platform}.zip"; \
    unzip -q /tmp/chrome-headless-shell.zip -d /opt; \
    mv "/opt/chrome-headless-shell-${platform}" /opt/chrome-headless-shell; \
    rm /tmp/chrome-headless-shell.zip

# Stage 3: runtime. No .NET runtime is needed: the server is a native binary.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble@sha256:099f6f87ed745377dd27bd722f0d1a352bca71b4fddaabfd75e7c064bcaa82da
# tini runs as PID 1: it forwards SIGTERM to the server (whose shutdown closes the browser cleanly)
# and reaps the browser's exited child processes, so none linger as zombies.
# The lib* packages are the browser's shared libraries that the base image lacks: the NEEDED entries
# of chrome-headless-shell (readelf -d). The linux64 and linux-arm64 builds differ (only linux64
# links libatk-bridge-2.0), so the ldd check below runs on every architecture.
RUN apt-get update && apt-get install -y --no-install-recommends \
    tini \
    fontconfig \
    fonts-liberation \
    fonts-noto-color-emoji \
    libasound2t64 \
    libatk-bridge2.0-0t64 \
    libatk1.0-0t64 \
    libatspi2.0-0t64 \
    libdbus-1-3 \
    libexpat1 \
    libgbm1 \
    libglib2.0-0t64 \
    libnspr4 \
    libnss3 \
    libx11-6 \
    libxcb1 \
    libxcomposite1 \
    libxdamage1 \
    libxext6 \
    libxfixes3 \
    libxkbcommon0 \
    libxrandr2 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=browser /opt/chrome-headless-shell /opt/chrome-headless-shell
# Fail the image build when the browser or a library it bundles needs a shared library that is not
# installed; otherwise it only shows when the browser first starts, as failed conversions.
RUN set -eu; \
    status=0; \
    for file in /opt/chrome-headless-shell/chrome-headless-shell /opt/chrome-headless-shell/*.so*; do \
      [ -e "$file" ] || continue; \
      libraries="$(ldd "$file")"; \
      missing="$(echo "$libraries" | grep 'not found' || true)"; \
      if [ -n "$missing" ]; then \
        echo "$file needs shared libraries this image does not install:" >&2; \
        echo "$missing" >&2; \
        status=1; \
      fi; \
    done; \
    exit "$status"
# The base image sets ASPNETCORE_HTTP_PORTS=8080, the port the server listens on.
ENV ReportsEngine__Browser__ExecutablePath=/opt/chrome-headless-shell/chrome-headless-shell
WORKDIR /app
COPY --from=build /app .
# The non-root user the .NET images provide, app (UID 1654), by number so that Kubernetes'
# runAsNonRoot can verify it. The browser runs with Chromium's own sandbox, which needs user
# namespaces that Docker's default seccomp profile denies: run the container with
# deploy/seccomp/chromium.json (docker run --security-opt seccomp=deploy/seccomp/chromium.json ...).
# Without it the browser cannot start and readiness reports why. Only for trusted HTML on a
# platform that cannot allow user namespaces, opt out with ReportsEngine__Browser__NoSandbox=true.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["/usr/bin/tini", "--", "./Atli.Reports.Server"]
