# syntax=docker/dockerfile:1
# Multi-stage build for PrintFarmer (Blazor WASM hosted, .NET 8)

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
# Kestrel default in container; override to 0.0.0.0 so it is reachable
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Prereqs for Emscripten toolchain used by wasm-tools (needs `python` on PATH)
RUN apt-get update \
	&& apt-get install -y --no-install-recommends python3 \
	&& ln -s /usr/bin/python3 /usr/local/bin/python \
	&& rm -rf /var/lib/apt/lists/*

# Copy solution first for better layer caching
COPY src/farm-web.sln ./
# Mirror the solution's folder casing (Linux is case-sensitive)
# Source on host may be lowercase; copy into the expected uppercase dirs in the container
COPY src/server/ ./server/
COPY src/client/ ./client/
COPY src/shared/ ./shared/

# Restore only the server project (and its project references) to avoid needing test projects
RUN dotnet restore ./server/Farm.Web.Server.csproj

# Ensure Blazor WebAssembly tooling is available in the SDK image
RUN dotnet workload install wasm-tools

# Publish the server (which hosts the client)
WORKDIR /src/server
RUN dotnet publish Farm.Web.Server.csproj -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .

# Important: The server hosts the Blazor WASM client from wwwroot when published
# If the app uses SQLite, the DB file will live inside the container filesystem
# unless mounted as a volume by the user.

ENTRYPOINT ["dotnet", "Farm.Web.Server.dll"]
