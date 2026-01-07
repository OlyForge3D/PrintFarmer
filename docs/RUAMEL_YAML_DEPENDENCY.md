# ruamel.yaml Python Module - Critical Deployment Dependency

**Date**: November 1, 2025  
**Severity**: CRITICAL  
**Impact**: Deployment failures with malformed Docker Compose YAML

## Overview

The `compose-generator.sh` script requires the Python `ruamel.yaml` module to properly generate Docker Compose files with correct YAML structure. **Without it, the deployment will FAIL with "services must be a mapping" error.**

## The Problem

When generating Docker Compose files for **microservices** architectures with non-SQLite database providers (PostgreSQL, SQL Server, MySQL):

1. **With ruamel.yaml installed** ✅
   - Python script properly parses and regenerates YAML
   - Database service is correctly indented under `services:` key
   - `docker compose config` validation passes
   - Deployment succeeds

2. **Without ruamel.yaml installed** ❌
   - Python script fails with `ImportError: No module named 'ruamel.yaml'`
   - Code falls back to AWK-based YAML manipulation (BROKEN)
   - Database service key ends up at wrong indentation level
   - Docker Compose fails: `services must be a mapping`
   - Deployment fails

## Why This Happens

The generated database configuration looks like:
```
database:
  image: postgres:15-alpine
  container_name: ...
  environment: ...
```

This needs to be inserted into the compose file with proper indentation:
```yaml
services:
  database:
    image: postgres:15-alpine
    container_name: ...
    environment: ...
```

The `ruamel.yaml` Python module understands YAML structure and properly handles this indentation. Without it, simple text manipulation produces invalid YAML:
```yaml
services:
database:   # ← WRONG INDENTATION!
  image: ...
```

## Installation

### Option 1: Using pip (Recommended)
```bash
pip install ruamel.yaml
```

### Option 2: System Package Manager (Debian/Ubuntu)
```bash
apt-get update
apt-get install python3-ruamel.yaml
```

### Option 3: MacOS with Homebrew
```bash
brew install python3
pip3 install ruamel.yaml
```

### Option 4: Docker (Already Installed)
If running in a Docker container, ensure the Dockerfile includes:
```dockerfile
RUN pip install ruamel.yaml
```

## Verification

Verify installation:
```bash
python3 -c "from ruamel.yaml import YAML; print('✓ ruamel.yaml is installed')"
```

Should output:
```
✓ ruamel.yaml is installed
```

## Deployment Checklist

Before running compose-generator or deploy-docker scripts:

- [ ] Python 3 is installed: `python3 --version`
- [ ] ruamel.yaml is installed: `python3 -c "from ruamel.yaml import YAML"`
- [ ] Docker/Docker Compose is installed (for validation)

## Testing

The deployment test suite checks for this dependency:

```bash
cd /Users/jpapiez/s/PFarm1
bash tests/run-deployment-tests.sh
```

The `ruamel_yaml_dependency_check` test will FAIL if the module is not installed, preventing you from accidentally attempting a deployment that will fail.

## Why No Fallback?

Previous versions had an AWK-based fallback that tried to handle YAML when Python failed. This fallback was **removed intentionally** because:

1. **It produced invalid YAML** - The indentation wasn't preserved
2. **It silently failed** - Tests would pass but deployment would fail
3. **It was hard to debug** - Users would see cryptic Docker errors instead of clear dependency messages

By requiring `ruamel.yaml` explicitly, we:
- ✅ Fail fast with a clear error message
- ✅ Prevent silent failures
- ✅ Make troubleshooting obvious
- ✅ Generate valid YAML every time

## Error Messages

If ruamel.yaml is missing, you'll see:

```
[ERROR] FATAL: Python module 'ruamel.yaml' is not installed
[ERROR]        This module is REQUIRED for proper Docker Compose YAML generation
[ERROR]        Installation: pip install ruamel.yaml
[ERROR]        Or for system-wide: apt-get install python3-ruamel.yaml (Debian/Ubuntu)
```

## Architecture-Specific Notes

- **Monolithic**: Uses SQLite, doesn't require ruamel.yaml (no database service to configure)
- **Microservices**: REQUIRES ruamel.yaml (needs database service generation)

## Historical Context

This dependency was identified during testing when:
1. Tests passed on development machines (where ruamel.yaml was installed)
2. Deployments failed on fresh VMs (where ruamel.yaml was not installed)
3. Root cause: Broken AWK fallback producing malformed YAML
4. Solution: Require ruamel.yaml explicitly with clear error messages

## Related Files

- `scripts/docker/compose-generator.sh` - Dependency check at line ~442
- `scripts/docker/compose-replace-db.py` - Uses ruamel.yaml for YAML manipulation
- `tests/test-compose-generator.sh` - `test_ruamel_yaml_dependency_check()` test

## See Also

- [ruamel.yaml Documentation](https://yaml.readthedocs.io/)
- [Python Package Index](https://pypi.org/project/ruamel.yaml/)
