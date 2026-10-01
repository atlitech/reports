FROM mcr.microsoft.com/devcontainers/dotnet:10.0

# Install Node and Bun
COPY tools/node_bun-install.sh /tmp/node_bun-install.sh
RUN su vscode -c "/bin/bash /tmp/node_bun-install.sh" 2>&1

# Install Chromium
COPY tools/chromium-install.sh /tmp/chromium-install.sh
RUN su vscode -c "/bin/bash /tmp/chromium-install.sh" 2>&1
