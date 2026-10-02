# Disposable NativeAOT stdio worker. Build from the repository root.
# Requires an external supported sandbox runtime; never expose a Docker socket to this image.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot@sha256:96f3b7d45f53eb05990f05b89ce61c4e23d07a5098521c2f20b018630e34f298 AS build
WORKDIR /src
# Restore from the project files alone, so a source change reuses the restored packages (the
# NativeAOT compiler among them). The compiler and runtime packs are per architecture, so restore
# and publish both target the build's own runtime: the stage runs on the image's architecture.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Atli.Reports.Engine/Atli.Reports.Engine.csproj src/Atli.Reports.Engine/
COPY src/Atli.Reports.Worker.Protocol/Atli.Reports.Worker.Protocol.csproj src/Atli.Reports.Worker.Protocol/
COPY src/Atli.Reports.Worker/Atli.Reports.Worker.csproj src/Atli.Reports.Worker/
RUN dotnet restore src/Atli.Reports.Worker --use-current-runtime
COPY src/Atli.Reports.Engine/ src/Atli.Reports.Engine/
COPY src/Atli.Reports.Worker.Protocol/ src/Atli.Reports.Worker.Protocol/
COPY src/Atli.Reports.Worker/ src/Atli.Reports.Worker/
RUN dotnet publish src/Atli.Reports.Worker -c Release --use-current-runtime --no-restore -o /app
# The native binary's debug symbols (.dbg, about 40 MB), the engine's symbols and XML docs: none
# is loaded at runtime. Only the executable and its runtime dependencies stay.
RUN rm -f /app/*.dbg /app/*.pdb /app/*.xml

# Stage 2: download chrome-headless-shell, Chromium's dedicated headless build. Compared with the full
# browser it starts faster, prints faster, and creates the per-conversion browser contexts the engine
# isolates documents with several times faster.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble@sha256:099f6f87ed745377dd27bd722f0d1a352bca71b4fddaabfd75e7c064bcaa82da AS browser
ARG TARGETARCH
# Derive the default browser pin from the server Dockerfile to avoid a second version source.
ARG CHROME_VERSION
COPY src/Atli.Reports.Server/Dockerfile /tmp/server-Dockerfile
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
    version="${CHROME_VERSION:-$(sed -n 's/^ARG CHROME_VERSION=//p' /tmp/server-Dockerfile)}"; \
    if [ "${version}" = "stable" ]; then \
      version="$(curl -fsSL https://googlechromelabs.github.io/chrome-for-testing/LATEST_RELEASE_STABLE)"; \
    fi; \
    curl -fsSLo /tmp/chrome-headless-shell.zip \
      "https://storage.googleapis.com/chrome-for-testing-public/${version}/${platform}/chrome-headless-shell-${platform}.zip"; \
    unzip -q /tmp/chrome-headless-shell.zip -d /opt; \
    mv "/opt/chrome-headless-shell-${platform}" /opt/chrome-headless-shell; \
    rm /tmp/chrome-headless-shell.zip

# Stage 3: runtime. No .NET runtime is needed: the worker is a native binary.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble@sha256:099f6f87ed745377dd27bd722f0d1a352bca71b4fddaabfd75e7c064bcaa82da
# The trusted launcher must supply --init to forward signals and reap browser child processes.
# The lib* packages are the browser's shared libraries that the base image lacks: the NEEDED entries
# of chrome-headless-shell (readelf -d). The linux64 and linux-arm64 builds differ (only linux64
# links libatk-bridge-2.0), so the ldd check below runs on every architecture.
RUN apt-get update && apt-get install -y --no-install-recommends \
    util-linux \
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
# Stdio only. The launcher supplies the outer gVisor sandbox and mandatory resource limits.
COPY --chmod=755 src/Atli.Reports.Worker/launch-browser.sh /usr/local/bin/atli-reports-browser
ENV ATLI_WORKER_BROWSER_PATH=/usr/local/bin/atli-reports-browser
ENV ATLI_WORKER_NO_SANDBOX=true
ENV ATLI_WORKER_DISABLE_DEV_SHM_USAGE=true
WORKDIR /app
COPY --from=build /app .
# The browser runs inside the outer gVisor sandbox; this image alone is not a security boundary.
USER $APP_UID
ENTRYPOINT ["./Atli.Reports.Worker"]
