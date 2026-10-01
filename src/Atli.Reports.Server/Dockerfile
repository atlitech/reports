# Builds the Atli Reports Server as a NativeAOT binary and runs it next to Chromium.
#
# The build context is the repository root:
#   docker build -f src/Atli.Reports.Server/Dockerfile -t atli-reports-server .
# or, from this directory:
#   docker compose up --build

# Stage 1: Build (NativeAOT)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
RUN apt-get update && apt-get install -y --no-install-recommends \
    clang \
    zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Atli.Reports.Engine/ src/Atli.Reports.Engine/
COPY src/Atli.Reports.Server/ src/Atli.Reports.Server/
RUN dotnet publish src/Atli.Reports.Server -c Release -o /app

# Stage 2: Runtime (no .NET runtime needed)
FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
    chromium \
    fonts-liberation \
    fonts-noto-color-emoji \
    && rm -rf /var/lib/apt/lists/*
ENV ReportsEngine__Browser__ExecutablePath=/usr/bin/chromium
ENV ASPNETCORE_HTTP_PORTS=8080
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["./Atli.Reports.Server"]
