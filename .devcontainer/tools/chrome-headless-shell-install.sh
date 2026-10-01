#!/bin/bash
# Installs chrome-headless-shell from Chrome for Testing, the browser the Atli Reports server image
# ships and the README recommends, with the libraries and fonts it needs. Runs as root.
#
# The engine finds Chrome or Chromium by itself, but not chrome-headless-shell, so a `chromium`
# link on the PATH lets the tests and the examples use it without any configuration.
set -euo pipefail

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

version="$(curl -fsSL https://googlechromelabs.github.io/chrome-for-testing/LATEST_RELEASE_STABLE)"
curl -fsSLo /tmp/chrome-headless-shell.zip \
  "https://storage.googleapis.com/chrome-for-testing-public/${version}/${platform}/chrome-headless-shell-${platform}.zip"
unzip -q /tmp/chrome-headless-shell.zip -d /opt
mv "/opt/chrome-headless-shell-${platform}" /opt/chrome-headless-shell
rm /tmp/chrome-headless-shell.zip
ln -s /opt/chrome-headless-shell/chrome-headless-shell /usr/local/bin/chromium
