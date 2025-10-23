# Docker Platform Configuration

## Overview
This document explains the platform configuration for PrintFarmer Docker builds and how to handle cross-platform builds.

## Platform Build Arguments

### Default Configuration
- **Default Platform**: `linux/amd64`
- **Reason**: Avoids ARM64 protoc crashes on Apple Silicon during .NET builds
- **Configurable**: Yes, via build arguments

### Build Arguments Available

#### `TARGETPLATFORM`
- **Purpose**: Specifies the target platform for the Docker image
- **Default**: `linux/amd64`
- **Usage**: `--build-arg TARGETPLATFORM=linux/arm64`

## Building for Different Platforms

### AMD64 (Default)
```bash
# Explicit AMD64 build
docker build -f Dockerfile.api --build-arg TARGETPLATFORM=linux/amd64 -t printfarmer-api .

# Or use default (no build arg needed)
docker build -f Dockerfile.api -t printfarmer-api .
```

### ARM64 (Apple Silicon native)
```bash
# ARM64 build (may have protoc issues)
docker build -f Dockerfile.api --build-arg TARGETPLATFORM=linux/arm64 -t printfarmer-api .
```

### Multi-platform builds
```bash
# Build for multiple platforms
docker buildx build --platform linux/amd64,linux/arm64 -f Dockerfile.api -t printfarmer-api .
```

## Docker Compose Integration

### Environment Variable Support
You can set the platform via environment variables:

```bash
# Set platform for all builds
export DOCKER_DEFAULT_PLATFORM=linux/amd64

# Or set in .env file
echo "DOCKER_DEFAULT_PLATFORM=linux/amd64" >> .env
```

### Docker Compose Override
Create a `docker-compose.override.yml` for platform-specific builds:

```yaml
services:
  api:
    build:
      args:
        TARGETPLATFORM: linux/arm64  # Override for ARM64
```

## Buildx Configuration

### Enable BuildKit (recommended)
```bash
export DOCKER_BUILDKIT=1
export COMPOSE_DOCKER_CLI_BUILD=1
```

### Multi-platform builder
```bash
# Create multi-platform builder
docker buildx create --name multiarch --driver docker-container --use
docker buildx inspect --bootstrap

# Build for multiple platforms
docker buildx build --platform linux/amd64,linux/arm64 -t printfarmer-api .
```

## Troubleshooting Platform Issues

### ARM64 Protoc Issues
If you encounter protoc crashes on ARM64:
1. Use the default AMD64 build
2. Or install protoc manually in the Dockerfile for ARM64

### BuildKit Warnings
The warnings you saw:
```
WARN: FromPlatformFlagConstDisallowed: FROM --platform flag should not use constant value "linux/amd64"
```

Are now resolved by using the `$TARGETPLATFORM` build argument instead of hardcoded values.

### Platform Detection
To detect the current platform in builds:
```dockerfile
ARG TARGETPLATFORM
RUN echo "Building for platform: $TARGETPLATFORM"
```

## Deploy Script Integration

The deploy script automatically uses the default platform configuration. To override:

```bash
# Set platform before running deploy script
export TARGETPLATFORM=linux/arm64
./scripts/deploy-docker.sh

# Or pass as environment variable
TARGETPLATFORM=linux/arm64 ./scripts/deploy-docker.sh
```

## Performance Considerations

### AMD64 on Apple Silicon
- Runs via emulation (Rosetta 2)
- Slightly slower but more compatible
- Recommended for production consistency

### ARM64 Native
- Faster on Apple Silicon
- May have compatibility issues with some packages
- Good for development on ARM-based systems

## Best Practices

1. **Use build arguments** instead of hardcoded platform flags
2. **Default to AMD64** for maximum compatibility
3. **Test on target platform** before production deployment
4. **Use multi-platform builds** for distribution
5. **Document platform requirements** for team members

## Migration Notes

### Old (Hardcoded)
```dockerfile
FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/aspnet:9.0 AS base
```

### New (Flexible)
```dockerfile
ARG TARGETPLATFORM=linux/amd64
FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
```

This change eliminates BuildKit warnings while maintaining the same default behavior and adding flexibility for different deployment scenarios.