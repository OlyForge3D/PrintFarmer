# syntax=docker/dockerfile:1
# Multi-stage build for PrintFarmer React + ASP.NET Core

# Stage 1: Build React application
FROM node:18-alpine AS react-build
WORKDIR /app

# Copy package files first for better caching
COPY src/Web/ReactApp/package*.json ./
RUN npm install --silent

# Copy React source code
COPY src/Web/ReactApp/ ./

# Build React application for production
RUN npm run build

# Stage 2: Build .NET API
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS dotnet-build
WORKDIR /src

# Copy project files for dependency restoration
COPY src/infra/*.csproj ./infra/
COPY src/import/*.csproj ./import/
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
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Install curl for health checks
RUN apt-get update && \
    apt-get install -y curl && \
    rm -rf /var/lib/apt/lists/*

# Copy .NET application
COPY --from=dotnet-build /app/publish .

# Copy React build output to wwwroot
COPY --from=react-build /app/dist ./wwwroot

# Create directories for file storage
RUN mkdir -p /app/uploads /app/gcode /app/slicers /data

# Set environment variables
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Expose ports
EXPOSE 8080

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Farm.Web.Api.dll"]
