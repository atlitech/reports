FROM mcr.microsoft.com/devcontainers/dotnet:10.0@sha256:9eb314432a5fa67773a53ab754cdc381cae614906162f9fc44cdb7f7837b91c7

# The build context is the repository root (see devcontainer.json);
# Atli.Reports.Dev.Dockerfile.dockerignore sends only the files below.

# Install Node and Bun
COPY .devcontainer/tools/node_bun-install.sh /tmp/node_bun-install.sh
RUN su vscode -c "/bin/bash /tmp/node_bun-install.sh" 2>&1

# Install chrome-headless-shell (as root: it installs system packages), the version the server image
# pins unless the CHROME_VERSION build argument says otherwise. The files keep their repository
# layout, which is how the install script finds the pin.
COPY src/Atli.Reports.Server/Dockerfile /tmp/atli/src/Atli.Reports.Server/Dockerfile
COPY .github/scripts/chrome-headless-shell.sh /tmp/atli/.github/scripts/chrome-headless-shell.sh
COPY .devcontainer/tools/chrome-headless-shell-install.sh /tmp/atli/.devcontainer/tools/chrome-headless-shell-install.sh
ARG CHROME_VERSION=
RUN CHROME_VERSION="${CHROME_VERSION}" /bin/bash /tmp/atli/.devcontainer/tools/chrome-headless-shell-install.sh \
  && rm -rf /tmp/atli
