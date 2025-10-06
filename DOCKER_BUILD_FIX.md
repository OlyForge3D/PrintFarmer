# Docker API Build Fix

## Problem
The Docker build for `Farm.Web.Api` was failing with two critical errors:

### Error 1: ARM64 protoc Crash
```
error MSB6006: "/root/.nuget/packages/grpc.tools/2.71.0/tools/linux_arm64/protoc" exited with code 139
```
**Cause**: On Apple Silicon (ARM64) Macs, Docker was defaulting to ARM64 architecture, but the gRPC protoc compiler for ARM64 was segfaulting (exit code 139 = segmentation fault).

### Error 2: Missing Infrastructure Project
```
error NETSDK1004: Assets file '/src/Infrastructure/obj/project.assets.json' not found. 
Run a NuGet package restore to generate this file.
```
**Cause**: Two issues:
1. The `.csproj` file had wrong case: `../infrastructure/Farm.Infrastructure.csproj` (should be `../Infrastructure/`)
2. The Dockerfile wasn't copying the Infrastructure project file before restore

## Solutions Applied

### 1. Fixed Infrastructure Project Reference
**File**: `src/api/Farm.Web.Api.csproj`
```xml
<!-- BEFORE (incorrect) -->
<ProjectReference Include="../infrastructure/Farm.Infrastructure.csproj" />

<!-- AFTER (correct) -->
<ProjectReference Include="../Infrastructure/Farm.Infrastructure.csproj" />
```

### 2. Updated Dockerfile to Include Infrastructure
**File**: `Dockerfile.api`
```dockerfile
# Added Infrastructure project to COPY step
COPY src/shared/*.csproj ./shared/
COPY src/api/*.csproj ./api/
COPY src/Infrastructure/*.csproj ./Infrastructure/  # ← Added this line
```

### 3. Fixed ARM64 protoc Issue with Platform Flag
**File**: `Dockerfile.api`
```dockerfile
# Force AMD64/x86_64 platform to avoid ARM64 protoc crash
FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/aspnet:9.0 AS base
# ...
FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/sdk:9.0 AS build
```

## Verification
Build now completes successfully:
```bash
docker build -f Dockerfile.api -t printfarmer-api:test .
# ✅ Build succeeds with warnings only (no errors)
```

## Notes
- The `FromPlatformFlagConstDisallowed` warnings can be ignored - they're Docker linting suggestions
- The build creates warnings about code analysis (CA1819, CA1054, etc.) - these are non-blocking
- **Platform consideration**: Building for AMD64 on ARM64 uses emulation (Rosetta), so it's slightly slower but works reliably

## Future Improvements (Optional)
1. **Make platform configurable** via build arg:
   ```dockerfile
   ARG BUILDPLATFORM=linux/amd64
   FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0 AS build
   ```

2. **Fix gRPC ARM64 support**: Once gRPC.Tools fixes the ARM64 protoc issue, remove platform flags

3. **Suppress code analysis warnings** in Docker builds:
   ```dockerfile
   RUN dotnet publish -c Release -o /app/publish --no-restore \
       /p:TreatWarningsAsErrors=false \
       /p:WarningLevel=0
   ```

## Related Files
- `Dockerfile.api` - Multi-stage Docker build for API
- `src/api/Farm.Web.Api.csproj` - API project file
- `src/Infrastructure/Farm.Infrastructure.csproj` - Infrastructure project
- `proto/slicer_jobs.proto` - gRPC service definitions
