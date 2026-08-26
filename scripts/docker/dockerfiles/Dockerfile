# syntax=docker/dockerfile:1
# Multi-stage build for PrintFarmer React + ASP.NET Core

ARG BUILD_VERSION=development
ARG VCS_REF=unknown
ARG SOURCE_REPOSITORY=""
ARG SOURCE_ARCHIVE_URL=""
ARG SBOM_URL=""

# Stage 1: Build React application
FROM node:24-alpine AS react-build
WORKDIR /app

# Copy package files first for better caching
COPY src/Web/ReactApp/package*.json ./
RUN npm install --silent

# Copy React source code
COPY src/Web/ReactApp/ ./

# Build React application for production
RUN npm run build

# Stage 2: Build .NET API
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /src

# Copy project files for dependency restoration
COPY src/infra/*.csproj ./infra/
COPY src/import/*.csproj ./import/
COPY src/modules/Farm.Modules.Abstractions/*.csproj ./modules/Farm.Modules.Abstractions/
COPY src/modules/Farm.Modules.SmartPlug/*.csproj ./modules/Farm.Modules.SmartPlug/
COPY src/modules/Farm.Modules.PrintQueue/*.csproj ./modules/Farm.Modules.PrintQueue/
COPY src/modules/Farm.Modules.Maintenance/*.csproj ./modules/Farm.Modules.Maintenance/
COPY src/modules/Farm.Modules.Identity/*.csproj ./modules/Farm.Modules.Identity/
COPY src/api/*.csproj ./api/
COPY Directory.Build.props ./
COPY global.json ./

# Restore dependencies
RUN dotnet restore ./api/Farm.Web.Api.csproj

# Copy source code
COPY src/ ./

# Publish the API
RUN dotnet publish ./api/Farm.Web.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    --verbosity minimal

# Stage 3: Final runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
ARG BUILD_VERSION
ARG VCS_REF
ARG SOURCE_REPOSITORY
ARG SOURCE_ARCHIVE_URL
ARG SBOM_URL
WORKDIR /app

LABEL org.opencontainers.image.title="PrintFarmer Monolith" \
      org.opencontainers.image.description="PrintFarmer API and web client monolith" \
      org.opencontainers.image.licenses="AGPL-3.0-only" \
      org.opencontainers.image.source="${SOURCE_REPOSITORY}" \
      org.opencontainers.image.revision="${VCS_REF}" \
      org.opencontainers.image.version="${BUILD_VERSION}" \
      org.printfarmer.source-archive="${SOURCE_ARCHIVE_URL}" \
      org.printfarmer.sbom="${SBOM_URL}"

# Install curl for health checks
RUN apt-get update && \
    apt-get install -y curl && \
    rm -rf /var/lib/apt/lists/*

# Copy .NET application
COPY --from=dotnet-build /app/publish .

# Copy React build output to wwwroot
COPY --from=react-build /app/dist ./wwwroot
COPY LICENSE /usr/share/licenses/printfarmer/LICENSE
COPY THIRD-PARTY-NOTICES.md /usr/share/licenses/printfarmer/THIRD-PARTY-NOTICES.md

# Create directories for file storage
RUN mkdir -p /app/uploads /app/gcode /app/slicers /data

# Set environment variables
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV PFARM__SourceInfo__Version=${BUILD_VERSION}
ENV PFARM__SourceInfo__Revision=${VCS_REF}
ENV PFARM__SourceInfo__RepositoryUrl=${SOURCE_REPOSITORY}
ENV PFARM__SourceInfo__SourceArchiveUrl=${SOURCE_ARCHIVE_URL}
ENV PFARM__SourceInfo__SbomUrl=${SBOM_URL}

# Expose ports
EXPOSE 8080

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Farm.Web.Api.dll"]
