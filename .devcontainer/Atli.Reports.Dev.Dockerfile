FROM mcr.microsoft.com/devcontainers/dotnet:10.0

# Install Node and Bun
COPY tools/node_bun-install.sh /tmp/node_bun-install.sh
RUN su vscode -c "/bin/bash /tmp/node_bun-install.sh" 2>&1

# Install chrome-headless-shell (as root: it installs system packages)
COPY tools/chrome-headless-shell-install.sh /tmp/chrome-headless-shell-install.sh
RUN /bin/bash /tmp/chrome-headless-shell-install.sh
