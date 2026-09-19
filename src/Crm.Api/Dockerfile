# syntax=docker/dockerfile:1
#
# Build context MUST be the repository root, e.g.:
#   docker build -f src/Crm.Api/Dockerfile -t crm-api .
# (Crm.Api references sibling projects under src/Modules and src/Shared, and relies on
# the repo-root Directory.Build.props / Directory.Packages.props for central package
# management, so the whole src/ tree - not just this folder - needs to be in the build
# context.)

# Pinned to the exact SDK patch in global.json (rather than the floating :10.0 tag): the
# SDK's default Roslyn analyzer set changes between patches, and a newer patch than what
# global.json pins can turn on new CA rules that break `TreatWarningsAsErrors` builds that
# were never validated against them.
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src

# Repo-root MSBuild config first: these rarely change, so they cache well independently
# of application source. .editorconfig is required too - it's where CA1716/CA1711/CA1000/
# CA1863/CA1859/etc. are demoted to `none`; without it those analyzers fall back to
# AnalysisLevel's default severity and TreatWarningsAsErrors fails the build.
COPY Directory.Build.props Directory.Packages.props global.json .editorconfig ./
COPY src/ src/

RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/Crm.Api/Crm.Api.csproj --locked-mode

RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish src/Crm.Api/Crm.Api.csproj \
      --configuration Release \
      --no-restore \
      --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS runtime
WORKDIR /app

# The base runtime image ships without curl; needed for the HEALTHCHECK below.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080

# The base image already defines an unprivileged "app" user (see
# https://github.com/dotnet/dotnet-docker/blob/main/documentation/README.security.md).
USER app

HEALTHCHECK --interval=30s --timeout=3s --start-period=20s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Crm.Api.dll"]
