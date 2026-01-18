# Slicer Worker CI Security & Efficiency Checks

This document describes the automated security scanning and image efficiency analysis for PrintFarmer's slicer worker containers.

## Overview

The **Slicer Worker Security & Efficiency** workflow provides automated CI checks for all slicer worker Docker images:
- **Security Scanning**: Trivy vulnerability scanning for OS and library packages
- **Efficiency Analysis**: Dive layer analysis to detect wasted space and optimize images  
- **Smoke Testing**: Basic container startup validation
- **Metrics Tracking**: Image size and waste metrics for trend analysis

This complements the manual-only **strict build workflows** (orcaslicer-strict-build.yml, prusaslicer-strict-build.yml) used for production deployments.

## Workflow Triggers

The workflow runs automatically on:
- **Pull Requests**: Changes to worker Dockerfiles, worker source code, or workflow file
- **Push to main**: After merging PR changes to track baseline metrics
- **Manual Dispatch**: For ad-hoc security audits

### Monitored Files

```
Dockerfile.orcaslicer
Dockerfile.prusaslicer
Dockerfile.slicer-base
src/orcaslicer-worker/**
src/prusaslicer-worker/**
src/worker-shared/**
```

## Security Scanning (Trivy)

### What is Trivy?

[Aqua Trivy](https://github.com/aquasecurity/trivy) is a comprehensive vulnerability scanner for containers and other artifacts. It detects:
- **OS packages**: Vulnerabilities in Debian/Ubuntu base images
- **Language libraries**: .NET runtime dependencies
- **Misconfigurations**: Docker best practices violations

### Scan Configuration

- **Vulnerability Types**: OS packages and libraries
- **Severity Levels**: CRITICAL, HIGH, MEDIUM (LOW also captured in SARIF)
- **Output Formats**:
  - **Table**: Human-readable console output for quick review
  - **SARIF**: Machine-readable format uploaded to GitHub Security tab
- **Failure Behavior**: Non-blocking (exit-code: 0) to gather baseline metrics without breaking builds

### Viewing Results

**Console Output**: Check the "Scan with Trivy (table output)" step in each job log.

**GitHub Security Tab**: Navigate to **Security → Code scanning alerts → Trivy results** for:
- Detailed vulnerability descriptions
- Affected packages and fixed versions
- CVSS scores and severity ratings
- Historical trend analysis

**SARIF Categories**:
- `trivy-slicer-base`: Base runtime image vulnerabilities
- `trivy-orcaslicer`: OrcaSlicer worker-specific issues
- `trivy-prusaslicer`: PrusaSlicer worker-specific issues

### Remediation Workflow

1. **Review Alerts**: Check Security tab for new HIGH/CRITICAL vulnerabilities
2. **Update Base Images**: Pull latest `mcr.microsoft.com/dotnet/aspnet:9.0` tag
3. **Rebuild Workers**: Trigger workflow to verify fixes
4. **Document Exceptions**: If false positives, add `.trivyignore` file with justification

## Efficiency Analysis (Dive)

### What is Dive?

[wagoodman/dive](https://github.com/wagoodman/dive) analyzes Docker image layers to identify wasted space from:
- Duplicate files across layers
- Deleted files that remain in previous layers
- Cache artifacts not cleaned up
- Unnecessary build dependencies in final stage

### Efficiency Metrics

Dive calculates **Image Efficiency Score** as:
```
efficiency = (total_size - wasted_space) / total_size
```

**Configuration** (`.dive-ci.yml`):
- **Minimum Efficiency**: 85% (15% waste tolerance)
- **Maximum Wasted Bytes**: 100MB absolute limit
- **User Layer Waste**: 20% max for user-added layers
- **Failure Behavior**: Non-blocking (`fail-on-wasted-bytes: false`) for baseline gathering

### Optimization Guidelines

**Common Waste Sources**:

1. **Multi-layer package installs**: Combine RUN commands
   ```dockerfile
   # ❌ Wasteful
   RUN apt-get update
   RUN apt-get install -y curl
   RUN rm -rf /var/lib/apt/lists/*  # Previous layers still have lists
   
   # ✅ Efficient
   RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*
   ```

2. **Large files in intermediate layers**: Use multi-stage builds
   ```dockerfile
   # ❌ Wasteful - build artifacts in final image
   FROM base AS build
   RUN dotnet publish  # Creates large obj/ and bin/
   # (no cleanup)
   
   # ✅ Efficient - artifacts left in build stage
   FROM base AS build
   RUN dotnet publish -o /app/publish
   FROM runtime
   COPY --from=build /app/publish .
   ```

3. **Cached AppImages**: Clean up download artifacts
   ```dockerfile
   RUN wget orcaslicer.AppImage && \
       extract_appimage && \
       rm -f orcaslicer.AppImage  # Remove immediately in same layer
   ```

### Viewing Efficiency Reports

**Console Output**: Check "Analyze image efficiency with Dive" step for layer-by-layer breakdown.

**Step Summary**: View "Export image size metrics" for total size in MB.

**Detailed Analysis**: Run Dive locally for interactive exploration:
```bash
docker build -f Dockerfile.orcaslicer -t orcaslicer-local .
dive orcaslicer-local
```

## Smoke Testing

Basic container validation ensures workers start successfully with minimal configuration:

```bash
docker run -d \
  -e Worker__ApiKey=test-key \
  -e Worker__ServiceEndpoint=http://localhost:5245 \
  orcaslicer-worker:scan-abc123
```

**Validated Behaviors**:
- Container starts without crashing
- .NET runtime initializes
- Environment variables accepted
- Logs confirm worker registration attempt (expected to fail - no real API)

## Image Size Tracking

Each job exports image size metrics to `$GITHUB_STEP_SUMMARY`:

```
slicer-base image size: 245 MB
OrcaSlicer worker image size: 512 MB
PrusaSlicer worker image size: 498 MB
```

**Historical Tracking**: Compare sizes across commits to detect regressions.

**Size Budgets** (recommended targets based on current architecture):
- **slicer-base**: < 300 MB (.NET 10 ASP.NET runtime + GTK/offscreen deps)
- **OrcaSlicer worker**: < 600 MB (base + .NET worker + OrcaSlicer AppImage)
- **PrusaSlicer worker**: < 600 MB (base + .NET worker + PrusaSlicer AppImage or Flatpak)

## Workflow Architecture

### Job Dependencies

```
build-and-scan-base (slicer-base)
  ├── build-and-scan-orcaslicer (parallel)
  └── build-and-scan-prusaslicer (parallel)
        └── security-summary (aggregates results)
```

### Build Strategy

**Stub Mode for CI Efficiency**: Workers are built with `ALLOW_STUB=true` to:
- Skip downloading large AppImage binaries (~200-400MB each)
- Reduce CI runtime from 30+ minutes to ~5 minutes per worker
- Focus on image structure and dependency scanning (slicer binary CVEs handled upstream)

**Production Builds**: Use strict manual workflows with `ALLOW_STUB=false` for real binaries.

### Parallel Execution

OrcaSlicer and PrusaSlicer jobs run in parallel after base image completes, optimizing CI time:
- **Total Runtime**: ~10-12 minutes (vs ~25+ sequential)
- **Cost Savings**: ~50% reduction in GitHub Actions minutes

## Integration with Existing Workflows

### Relationship to containers.yml

The **containers.yml** workflow handles API and frontend images with similar security scanning. Key differences:

| Feature | containers.yml (API/Frontend) | slicer-worker-security.yml (Workers) |
|---------|-------------------------------|--------------------------------------|
| **Triggers** | Push to main, PR, tags | PR changes, push to main, manual |
| **Image Registry** | GitHub Container Registry (GHCR) | Local build only (no push) |
| **Signing** | Cosign + SLSA attestation | Not signed (dev/scan only) |
| **Trivy** | ✅ (with SARIF upload) | ✅ (with SARIF upload) |
| **Dive** | ❌ | ✅ |
| **Grype** | ✅ | ❌ (Trivy sufficient) |
| **SBOM** | ✅ (Syft) | ❌ (future enhancement) |

### Relationship to Strict Build Workflows

**orcaslicer-strict-build.yml** and **prusaslicer-strict-build.yml** remain **manual-only** with safety guards:

```yaml
on:
  workflow_dispatch:
    inputs:
      disable_slicer_builds:
        default: 'true'  # Requires explicit override to run
```

**Purpose**: Strict builds download real slicer binaries for production deployment. This security workflow provides **fast feedback** during development without triggering expensive binary downloads on every commit.

## Maintenance & Evolution

### Enabling Failure on Security Issues

Once baseline metrics are established, enable strict failure modes:

**Trivy** (in slicer-worker-security.yml):
```yaml
- name: Scan with Trivy
  uses: aquasecurity/trivy-action@0.28.0
  with:
    exit-code: '1'  # Change from '0' to fail on HIGH/CRITICAL
    severity: 'CRITICAL,HIGH'
```

**Dive** (in .dive-ci.yml):
```yaml
ci:
  fail-on-wasted-bytes: true  # Change from false
```

### Version Updates

**Trivy Action**: Update `aquasecurity/trivy-action@0.28.0` to latest
**Dive Action**: Use the `wagoodman/dive` container directly (or update `MartinHeinz/dive-action` reference). The workflow now runs the `wagoodman/dive:0.10.0` container to analyze images without depending on the marketplace action.
**Slicer Versions**: Update `ORCASLICER_VERSION` and `PRUSASLICER_VERSION` env vars in workflow

### Adding New Worker Types

To add security scanning for additional slicer workers (e.g., Cura, Simplify3D):

1. **Add Dockerfile**: Create `Dockerfile.curra-worker` based on slicer-base
2. **Update Workflow**: Add new job in `slicer-worker-security.yml`:
   ```yaml
   build-and-scan-cura:
     name: Build & Scan Cura Worker
     needs: build-and-scan-base
     steps:
       # ... similar to orcaslicer job
   ```
3. **Update Triggers**: Add new Dockerfile to `paths:` filter

## Troubleshooting

### Trivy Scan Fails with API Rate Limit

**Symptom**: `GET https://ghcr.io/v2/aquasecurity/trivy-db/manifests/...: 429 Too Many Requests`

**Solution**: Authenticate Trivy with GitHub token:
```yaml
- name: Scan with Trivy
  uses: aquasecurity/trivy-action@0.28.0
  env:
    TRIVY_USERNAME: ${{ github.actor }}
    TRIVY_PASSWORD: ${{ secrets.GITHUB_TOKEN }}
```

### Dive Action Fails on Stub Images

**Symptom**: Dive detects 90%+ wasted space due to stub binary

**Solution**: Already handled with `continue-on-error: true`. Dive analysis is most valuable for production strict builds, not stub mode.

### SARIF Upload Fails

**Symptom**: `github/codeql-action/upload-sarif@v3` fails with permissions error

**Solution**: Ensure `security-events: write` permission in workflow:
```yaml
permissions:
  contents: read
  security-events: write
```

### Image Size Regression

**Symptom**: Worker image size increases significantly (e.g., slicer-base jumps from 245MB → 380MB)

**Investigation Steps**:
1. **Check Dive report** for new wasted layers
2. **Review recent Dockerfile changes** with `git diff`
3. **Compare layer sizes** with `docker history <image>`
4. **Identify culprit**: Often new apt packages or cached downloads

## Security Best Practices

### Vulnerability Remediation Priority

1. **CRITICAL**: Fix immediately (within 24 hours)
2. **HIGH**: Fix in next release cycle (within 1 week)
3. **MEDIUM**: Track and fix in next minor version
4. **LOW**: Monitor, fix opportunistically

### Exception Handling

If a vulnerability cannot be fixed (e.g., upstream not patched, false positive):

1. **Document in `.trivyignore`**:
   ```
   # CVE-2024-12345: False positive - affects Windows only, not Linux containers
   CVE-2024-12345
   ```

2. **Add comment in Dockerfile**:
   ```dockerfile
   # SECURITY NOTE: Using dotnet/aspnet:9.0 despite CVE-2024-12345
   # Rationale: Vulnerability affects Windows hosts only; mitigated by Linux container isolation
   FROM mcr.microsoft.com/dotnet/aspnet:9.0
   ```

3. **Track in security issue**: Create GitHub issue with `security` label

### Supply Chain Security

- **Base Images**: Use official Microsoft .NET images (GHCR `mcr.microsoft.com`)
- **Dependency Pinning**: Pin slicer versions in Dockerfiles (not `latest`)
- **Image Signing**: Future enhancement - add Cosign signing for worker images
- **SBOM Generation**: Future enhancement - add Syft SBOM for workers

## References

- **Trivy Documentation**: https://aquasecurity.github.io/trivy/
- **Dive Documentation**: https://github.com/wagoodman/dive
- **Docker Best Practices**: https://docs.docker.com/develop/dev-best-practices/
- **SARIF Specification**: https://docs.oasis-open.org/sarif/sarif/v2.1.0/
- **Worker Sandboxing Guide**: See `docs/WORKER_SANDBOXING.md`

## Future Enhancements

### Planned Improvements

- [ ] **SBOM Generation**: Add Syft SBOM generation for worker images (like containers.yml)
- [ ] **Grype Scanning**: Add Anchore Grype as secondary scanner for comparison
- [ ] **Image Signing**: Add Cosign signing for production worker images
- [ ] **Strict Failure Modes**: Enable `exit-code: 1` for Trivy and Dive after baseline established
- [ ] **Performance Benchmarks**: Add container startup time and memory footprint metrics
- [ ] **Multi-arch Builds**: Extend to linux/arm64 for Raspberry Pi deployments

### Experimental Features

- **Dockle Linting**: Docker image linter for best practice compliance
- **Hadolint**: Dockerfile linter integrated into workflow
- **Container Structure Tests**: Automated testing framework for image validation
- **Falco Runtime Rules**: Runtime security monitoring for production workers

---

**Last Updated**: 2025-01-09  
**Workflow Version**: 1.0.0  
**Maintainer**: PrintFarmer Security Team
