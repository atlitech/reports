#!/bin/bash
# Installs chrome-headless-shell from Chrome for Testing, the browser the Atli Reports server image
# ships and the README recommends, with the libraries and fonts it needs. Runs as root.
#
# The version is the one the server image pins (src/Atli.Reports.Server/Dockerfile, read through
# .github/scripts/chrome-headless-shell.sh), so the dev container tests against the same browser.
# Both files are looked up relative to this script, as in the repository. CHROME_VERSION overrides
# it: a version such as 154.0.8037.92, or `stable` for Chrome for Testing's current stable release
# (in the dev container, set it under build.args in devcontainer.json).
#
# The engine finds Chrome or Chromium by itself, but not chrome-headless-shell, so a `chromium`
# link on the PATH lets the tests and the examples use it without any configuration.
set -euo pipefail

repository="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
version="${CHROME_VERSION:-}"
if [ -z "$version" ]; then
  version="$(bash "$repository/.github/scripts/chrome-headless-shell.sh" pinned)"
fi

apt-get update
apt-get install -y --no-install-recommends \
  ca-certificates \
  curl \
  unzip \
  fontconfig \
  fonts-liberation \
  fonts-noto-color-emoji \
  fonts-roboto \
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
  libxrandr2
rm -rf /var/lib/apt/lists/*

case "$(dpkg --print-architecture)" in
  amd64) platform=linux64 ;;
  arm64) platform=linux-arm64 ;;
  *)
    echo "Unsupported architecture: $(dpkg --print-architecture)" >&2
    exit 1
    ;;
esac

if [ "$version" = stable ]; then
  version="$(curl -fsSL https://googlechromelabs.github.io/chrome-for-testing/LATEST_RELEASE_STABLE)"
fi
echo "Installing chrome-headless-shell $version ($platform)"
curl -fsSLo /tmp/chrome-headless-shell.zip \
  "https://storage.googleapis.com/chrome-for-testing-public/${version}/${platform}/chrome-headless-shell-${platform}.zip"
unzip -q /tmp/chrome-headless-shell.zip -d /opt
mv "/opt/chrome-headless-shell-${platform}" /opt/chrome-headless-shell
rm /tmp/chrome-headless-shell.zip
ln -s /opt/chrome-headless-shell/chrome-headless-shell /usr/local/bin/chromium
