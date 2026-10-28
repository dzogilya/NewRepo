# syntax=docker/dockerfile:1

# Build Stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project definitions
COPY Directory.Build.props .
COPY src/Fractals.Core/*.csproj src/Fractals.Core/
COPY src/Fractals.Data/*.csproj src/Fractals.Data/
COPY src/Fractals.Api/*.csproj src/Fractals.Api/
COPY src/Fractals.AppServer/*.csproj src/Fractals.AppServer/
COPY src/Fractals.Autoscaler/*.csproj src/Fractals.Autoscaler/

# Restore packages
RUN dotnet restore src/Fractals.Api/Fractals.Api.csproj
RUN dotnet restore src/Fractals.AppServer/Fractals.AppServer.csproj
RUN dotnet restore src/Fractals.Autoscaler/Fractals.Autoscaler.csproj

# Copy source code
COPY src/ src/

# Publish API
FROM build AS publish-api
RUN dotnet publish src/Fractals.Api/Fractals.Api.csproj -c Release -o /app/publish/api

# Publish AppServer
FROM build AS publish-appserver
RUN dotnet publish src/Fractals.AppServer/Fractals.AppServer.csproj -c Release -o /app/publish/appserver

# Publish Autoscaler
FROM build AS publish-autoscaler
RUN dotnet publish src/Fractals.Autoscaler/Fractals.Autoscaler.csproj -c Release -o /app/publish/autoscaler

# API Runtime Image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS api
WORKDIR /app
COPY --from=publish-api /app/publish/api .
USER $APP_UID
ENTRYPOINT ["dotnet", "Fractals.Api.dll"]

# AppServer Runtime Image (Worker)
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS appserver
WORKDIR /app
# Native dependencies for image processing (libfontconfig1, etc.)
USER root
RUN apt-get update && apt-get install -y --no-install-recommends libfontconfig1 libfreetype6 && rm -rf /var/lib/apt/lists/*
COPY --from=publish-appserver /app/publish/appserver .
ENTRYPOINT ["dotnet", "Fractals.AppServer.dll"]

# Autoscaler Runtime Image
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS autoscaler
WORKDIR /app
COPY --from=publish-autoscaler /app/publish/autoscaler .
ENTRYPOINT ["dotnet", "Fractals.Autoscaler.dll"]
