# Builds the Atli Reports Server as a NativeAOT binary and runs it next to chrome-headless-shell.
#
# The build context is the repository root:
#   docker build -f src/Atli.Reports.Server/Dockerfile -t atli-reports-server .
# or, from this directory:
#   docker compose up --build
#
# Build arguments:
#   CHROME_VERSION  The chrome-headless-shell version from Chrome for Testing, for example
#                   154.0.8037.92. Defaults to the current stable release, so a rebuild picks up
#                   browser security fixes; pin it for reproducible images.

# Stage 1: build (NativeAOT). The build and runtime stages share Ubuntu 24.04, so the binary runs
# against the glibc it was linked with.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
RUN apt-get update && apt-get install -y --no-install-recommends \
    clang \
    zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Atli.Reports.Engine/ src/Atli.Reports.Engine/
COPY src/Atli.Reports.Server/ src/Atli.Reports.Server/
RUN dotnet publish src/Atli.Reports.Server -c Release -o /app

# Stage 2: download chrome-headless-shell, Chromium's dedicated headless build. Compared with the full
# browser it starts faster, prints faster, and creates the per-conversion browser contexts the engine
# isolates documents with several times faster.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble AS browser
ARG TARGETARCH
ARG CHROME_VERSION=stable
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
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble
# tini runs as PID 1: it forwards SIGTERM to the server (whose shutdown closes the browser cleanly)
# and reaps the browser's exited child processes, so none linger as zombies.
RUN apt-get update && apt-get install -y --no-install-recommends \
    tini \
    fontconfig \
    fonts-liberation \
    fonts-noto-color-emoji \
    libasound2t64 \
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
ENV ReportsEngine__Browser__ExecutablePath=/opt/chrome-headless-shell/chrome-headless-shell
ENV ASPNETCORE_HTTP_PORTS=8080
WORKDIR /app
COPY --from=build /app .
# The non-root user the .NET images provide. The browser runs without its sandbox
# (ReportsEngine:Browser:NoSandbox in appsettings.json), which only suits trusted HTML.
USER app
EXPOSE 8080
ENTRYPOINT ["/usr/bin/tini", "--", "./Atli.Reports.Server"]
