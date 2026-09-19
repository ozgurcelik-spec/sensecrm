# syntax=docker/dockerfile:1
#
# Build context MUST be the repository root (same reasons as src/Crm.Api/Dockerfile):
#   docker build -f src/Crm.Worker/Dockerfile -t crm-worker .

# Pinned to the exact SDK patch in global.json - see src/Crm.Api/Dockerfile for why.
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json .editorconfig ./
COPY src/ src/

RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/Crm.Worker/Crm.Worker.csproj --locked-mode

RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish src/Crm.Worker/Crm.Worker.csproj \
      --configuration Release \
      --no-restore \
      --output /app/publish

# aspnet rather than runtime: Crm.Shared.Infrastructure references the ASP.NET Core shared framework.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENV DOTNET_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true

USER app

# The worker has no HTTP endpoint; HeartbeatService refreshes /tmp/crm-worker.heartbeat every 15 s. A file that is older than
# 60 s (or missing) means the process hung or is not polling, and the container turns unhealthy.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD test -f /tmp/crm-worker.heartbeat && test "$(( $(date +%s) - $(stat -c %Y /tmp/crm-worker.heartbeat) ))" -lt 60 || exit 1

ENTRYPOINT ["dotnet", "Crm.Worker.dll"]
