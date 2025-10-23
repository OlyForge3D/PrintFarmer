# Docker Scripts Refactoring

## Overview
The Docker utility scripts have been refactored to eliminate code duplication and ensure consistent behavior across all scripts that manage Docker containers and images.

## Changes Made

### New Shared Library: `scripts/docker-utils.sh`

**Purpose**: Centralized Docker management functions that can be shared across multiple scripts.

**Key Functions**:
- `docker_cleanup_container()` - Stop and remove individual containers
- `docker_force_remove_matching_containers()` - Pattern-based container removal
- `docker_cleanup_problematic_containers()` - Clean up known problematic containers
- `docker_cleanup_printfarmer_containers()` - Clean up all PrintFarmer containers
- `docker_comprehensive_cleanup()` - Full cleanup with progressive force escalation
- `docker_cleanup_printfarmer_images()` - Remove PrintFarmer images
- `docker_check_port_conflicts()` - Check for port usage conflicts
- `docker_system_cleanup()` - Docker system prune operations
- `docker_show_status()` - Display current Docker status

**Features**:
- **Consistent error handling** - All functions use `|| true` for safe execution
- **Audit logging** - All operations are logged to `.docker-ops-audit.log`
- **Force removal support** - Progressive escalation from gentle to force removal
- **Pattern matching** - Removes containers by name AND image patterns
- **Colored output** - Consistent visual feedback across scripts

### Updated Scripts

#### `scripts/cleanup-docker.sh`
**Before**: 93 lines with custom cleanup logic
**After**: 45 lines using shared utilities

**Improvements**:
- Uses `docker_cleanup_problematic_containers()` and `docker_cleanup_printfarmer_containers()`
- Replaced custom port checking with `docker_check_port_conflicts()`
- Added `docker_show_status()` for better visibility
- Consistent force removal using `docker_force_remove_matching_containers()`
- Same functionality, more reliable execution

#### `scripts/deploy-docker.sh`
**Before**: 2820 lines with duplicate force removal logic
**After**: 2801 lines using shared utilities

**Improvements**:
- Removed duplicate `force_remove_matching_containers()` function
- Uses `docker_force_remove_matching_containers()` from shared library
- Uses `docker_cleanup_printfarmer_images()` for consistent image removal
- Uses `docker_system_cleanup()` for unified prune operations
- Maintains all existing functionality while reducing code duplication

## Benefits

### 1. **Code Reuse**
- Eliminated ~50 lines of duplicate container removal logic
- Single source of truth for Docker operations
- Consistent behavior across all scripts

### 2. **Improved Reliability**
- Force removal patterns now match between scripts
- Better error handling with progressive escalation
- Comprehensive container cleanup that handles edge cases

### 3. **Easier Maintenance**
- Bug fixes only need to be made in one place
- New Docker management features can be added to the shared library
- Consistent audit logging across all operations

### 4. **Better User Experience**
- Consistent colored output and messaging
- Better status reporting with `docker_show_status()`
- Port conflict detection helps with troubleshooting

## Usage Examples

### Using the shared library in new scripts:
```bash
#!/bin/bash
# Source the shared Docker utilities
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/docker-utils.sh"

# Use the shared functions
docker_cleanup_printfarmer_containers force
docker_check_port_conflicts
docker_system_cleanup aggressive
```

### Available force removal options:
```bash
# Gentle cleanup (stop then remove)
docker_cleanup_printfarmer_containers

# Force cleanup (docker rm -f)
docker_cleanup_printfarmer_containers force

# Comprehensive cleanup with pattern matching
docker_comprehensive_cleanup force
```

## Backward Compatibility

- **No breaking changes** - All existing script interfaces remain the same
- **Same command-line arguments** - Scripts accept the same parameters
- **Same behavior** - Scripts produce the same results, just more reliably
- **Enhanced functionality** - Better error handling and status reporting

## Testing

Both scripts have been tested to ensure:
1. **Functional equivalence** - Same cleanup results as before
2. **Error handling** - Graceful handling of missing containers/images
3. **Force removal** - Proper escalation when normal removal fails
4. **Audit logging** - All operations are logged for troubleshooting
5. **Port conflict detection** - Helps identify blocking processes

## Future Enhancements

The shared library makes it easy to add new features:
- **Container health monitoring** - Check container health status
- **Volume cleanup** - Smart volume cleanup based on usage
- **Image optimization** - Remove unused layers and optimize storage
- **Network diagnostics** - Advanced network troubleshooting
- **Resource monitoring** - Track Docker resource usage

## Migration Notes

No migration is required for existing deployments. The refactored scripts are drop-in replacements that provide the same functionality with improved reliability and consistency.

The shared library can be used by future scripts that need Docker management capabilities, ensuring consistent behavior across the entire PrintFarmer ecosystem.