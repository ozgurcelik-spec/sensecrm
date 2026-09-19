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
    dotnet restore src/Crm.Worker/Crm.Worker.csproj

RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish src/Crm.Worker/Crm.Worker.csproj \
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

ENTRYPOINT ["dotnet", "Crm.Worker.dll"]
