# Deploy Script Fixes - October 2025

## Issues Fixed

### Issue 1: Script Failing with "failed to build docker images"
**Problem**: The deployment script was exiting immediately without actually attempting to build Docker containers.

**Root Causes**:
1. **.NET SDK check running for all architectures**: The `check_dotnet_sdk()` function was being called in `detect_environment()` for ALL deployments, but it's only needed for monolithic architecture.
2. **Unbound variable error**: `DB_PASSWORD` was not set for SQLite (monolithic) deployments, causing script to fail with `set -euo pipefail` strictness.

### Issue 2: .NET SDK Check Should Only Run for Monolithic
**Problem**: The .NET SDK is only needed for local development with monolithic architecture, but was being checked for all deployments including microservices.

**Solution**: Moved the .NET SDK check from `detect_environment()` to the monolithic architecture branch in `choose_architecture()`.

## Changes Made

### 1. Removed .NET SDK Check from Environment Detection
**File**: `scripts/deploy-docker.sh` (lines ~280-285)

```bash
# BEFORE:
detect_environment() {
    # ... other checks ...
    
    # Check .NET SDK (optional but recommended for local builds)
    check_dotnet_sdk
}

# AFTER:
detect_environment() {
    # ... other checks ...
    
    # (removed check_dotnet_sdk call)
}
```

### 2. Added .NET SDK Check Only for Monolithic Architecture
**File**: `scripts/deploy-docker.sh` (lines ~425-435)

```bash
# BEFORE:
case "$ARCH_CHOICE" in
    1|monolithic|mono)
        ARCHITECTURE="monolithic"
        ENV_FILE=".env.monolithic"
        COMPOSE_FILE="docker-compose.yml"
        print_success "Selected: Monolithic deployment"
        ;;

# AFTER:
case "$ARCH_CHOICE" in
    1|monolithic|mono)
        ARCHITECTURE="monolithic"
        ENV_FILE=".env.monolithic"
        COMPOSE_FILE="docker-compose.yml"
        print_success "Selected: Monolithic deployment"
        
        # Check .NET SDK for monolithic (optional but recommended for local builds)
        check_dotnet_sdk
        ;;
```

### 3. Fixed DB_PASSWORD Unbound Variable Error
**File**: `scripts/deploy-docker.sh` (line ~159)

```bash
# BEFORE:
DB_PASSWORD=$DB_PASSWORD

# AFTER:
DB_PASSWORD=${DB_PASSWORD:-}
```

**Explanation**: Using `${DB_PASSWORD:-}` provides an empty default if the variable is unset, preventing the "unbound variable" error when using SQLite (which doesn't require a password).

## Testing Results

### Before Fixes:
```bash
./scripts/deploy-docker.sh --non-interactive --dry-run
# Result: Script hangs or fails with "DB_PASSWORD: unbound variable"
```

### After Fixes:
```bash
./scripts/deploy-docker.sh --non-interactive --dry-run
# Result: ✅ Setup completed successfully! 🎉
```

## Behavior Summary

### .NET SDK Check Behavior:

| Architecture | .NET SDK Check | Reason |
|-------------|---------------|--------|
| Monolithic | ✅ **YES** | Needed for local development builds |
| Microservices | ❌ **NO** | All builds happen in Docker containers |

### Database Password Behavior:

| Database | DB_PASSWORD Set | Used In |
|----------|----------------|---------|
| SQLite (monolithic) | ❌ No (empty) | N/A (file-based) |
| PostgreSQL (microservices) | ✅ Yes | Container env + connection string |
| SQL Server (microservices) | ✅ Yes | Container env + connection string |
| MySQL (microservices) | ✅ Yes | Container env + connection string |
| External (microservices) | ⚠️ Maybe | Connection string only |

## Related Files
- `scripts/deploy-docker.sh` - Main deployment script
- `DOCKER_DEPLOYMENT.md` - Deployment documentation
- `DOTNET_SDK_INSTALLATION.md` - .NET SDK installation guide
- `.deploy-config` - Saved deployment configuration (gitignored)

## Future Improvements
1. Consider making .NET SDK check optional even for monolithic if users prefer pure Docker workflow
2. Add validation to ensure DB_PASSWORD is set when required
3. Improve error messages when script fails
