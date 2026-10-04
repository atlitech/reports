# Builds atli-reports-provisioner and runs its provisioning service (serve); see README.md.
#
# The build context is the repository root:
#   docker build -f src/Atli.Reports.Provisioner/Dockerfile -t atli-reports-provisioner .
#
# The image runs `atli-reports-provisioner serve`; settings come from Provisioner__* environment
# variables. It listens on 8080 (the base image's ASPNETCORE_HTTP_PORTS) over plain HTTP: run it on
# internal ingress only, behind TLS the platform terminates. Other commands run with the entrypoint
# overridden, such as `docker run --entrypoint dotnet ... atli-reports-provisioner.dll list`.
#
# The base images are pinned by the digest of their multi-arch index; update the tag and digest
# together. CI does not build this image.

# Stage 1: build. Framework-dependent: the runtime stage brings ASP.NET Core.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src
# Restore from the project files alone, so a source change reuses the restored packages.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Atli.Reports.Hosting/Atli.Reports.Hosting.csproj src/Atli.Reports.Hosting/
COPY src/Atli.Reports.Provisioner/Atli.Reports.Provisioner.csproj src/Atli.Reports.Provisioner/
RUN dotnet restore src/Atli.Reports.Provisioner
COPY src/Atli.Reports.Hosting/ src/Atli.Reports.Hosting/
COPY src/Atli.Reports.Provisioner/ src/Atli.Reports.Provisioner/
# No apphost: the entrypoint runs the assembly with the image's dotnet. XML docs are not loaded at
# runtime; the symbols stay, for line numbers in logged stack traces.
RUN dotnet publish src/Atli.Reports.Provisioner -c Release --no-restore -p:UseAppHost=false -o /app \
    && rm -f /app/*.xml

# Stage 2: runtime. Chiseled: no shell, no package manager, and only the libraries .NET needs. The
# provisioner runs with invariant globalization, so it needs no ICU.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:48e51f2f6798897be7ac4e775c049ed8fe60d3190f637e1f9c9dc7513efa659c
WORKDIR /app
COPY --from=build /app .
# The non-root user the .NET images provide, app (UID 1654), by number so that Kubernetes'
# runAsNonRoot can verify it. The process is PID 1 and handles SIGTERM itself: it stops taking
# requests, cancels the creations in flight, and exits once each has deleted its sandbox.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "atli-reports-provisioner.dll", "serve"]
