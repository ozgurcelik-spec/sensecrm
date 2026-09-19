# syntax=docker/dockerfile:1
#
# Run-once job image: applies the EF migrations of every module (`migrate`, the default) and bootstraps the first platform
# admin (`create-platform-admin`, reads PLATFORM_ADMIN_* environment variables). Build context MUST be the repository root:
#   docker build -f src/Crm.Migrator/Dockerfile -t crm-migrator .

# Pinned to the exact SDK patch used to develop (see src/Crm.Api/Dockerfile for why).
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json .editorconfig ./
COPY src/ src/

RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/Crm.Migrator/Crm.Migrator.csproj

RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish src/Crm.Migrator/Crm.Migrator.csproj \
      --configuration Release \
      --no-restore \
      --output /app/publish

# aspnet rather than runtime: Crm.Shared.Infrastructure references the ASP.NET Core shared framework.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENV DOTNET_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true

USER app

# No HEALTHCHECK on purpose: this is a run-once job, its exit code is the health signal.
ENTRYPOINT ["dotnet", "Crm.Migrator.dll"]
CMD ["migrate"]
