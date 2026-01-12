# Docker Compose --profile Flag Fix

## Date: October 6, 2025

## Problem

Deploy script was failing with error:
```
unknown flag: --profile
```

## Root Cause

The `--profile` flag was being passed as an argument to the `up` subcommand:

**❌ INCORRECT:**
```bash
docker compose --env-file .env.monolithic \
  -f docker-compose.yml \
  up -d --profile orca --profile prusa
#         ^^^^^^ WRONG POSITION!
```

In Docker Compose, the `--profile` flag must come BEFORE the subcommand (like `up`, `build`, etc.), not after it.

**✅ CORRECT:**
```bash
docker compose --env-file .env.monolithic \
  -f docker-compose.yml \
  --profile orca --profile prusa \
  up -d
#  ^^^ RIGHT POSITION!
```

## Fix Applied

### File: `scripts/deploy-docker.sh`

**Before (lines ~1310-1340):**
```bash
# Build profiles array
local profiles_to_enable=()
if [ "$ENABLE_ORCA_WORKER" = "yes" ]; then
    profiles_to_enable+=(--profile orca)
fi

# Pass profiles as arguments to 'up'  ❌ WRONG
"${compose_cmd[@]}" up -d "${profiles_to_enable[@]}"
```

**After:**
```bash
# Build complete compose command with profiles BEFORE the 'up' subcommand ✅
local final_compose_cmd=("${compose_cmd[@]}")

if [ "$ENABLE_ORCA_WORKER" = "yes" ] && [ "$ORCA_WORKER_COUNT" -gt 0 ]; then
    final_compose_cmd+=(--profile orca)
fi
if [ "$ENABLE_PRUSA_WORKER" = "yes" ] && [ "$PRUSA_WORKER_COUNT" -gt 0 ]; then
    final_compose_cmd+=(--profile prusa)
fi

# Now execute with correct flag order ✅
"${final_compose_cmd[@]}" up -d
```

## Docker Compose Command Structure

```
docker compose [GLOBAL OPTIONS] [SUBCOMMAND] [SUBCOMMAND OPTIONS]
               ^^^^^^^^^^^^^^^^  ^^^^^^^^^^^  ^^^^^^^^^^^^^^^^^^^
               Includes:         up, build,   -d, --scale, etc.
               --profile         down, etc.
               --env-file
               -f <file>
```

### Global Options (before subcommand):
- `--profile <name>` - Activate profiles
- `--env-file <file>` - Environment file
- `-f <file>` - Compose file(s)
- `--project-name` - Project name

### Subcommands:
- `up` - Start services
- `down` - Stop and remove services
- `build` - Build images
- `ps` - List containers

### Subcommand Options (after subcommand):
- `-d` - Detached mode
- `--scale <service>=<count>` - Scale services
- `--no-cache` - Don't use cache when building

## Testing & Validation

### Test Command:
```bash
printf "1\n8080\nProduction\nno\nno\nyes\nyes\n1\nyes\n1\nno\n\n" | \
  ./scripts/deploy-docker.sh --dry-run 2>&1 | \
  grep "Would run:"
```

### Expected Output:
```
✅ Would run: docker compose --env-file .env.monolithic \
    -f docker-compose.host-network.yml \
    -f docker-compose.override.yml \
    --profile prusa \
    up -d
```

**Flag order is correct!** `--profile` comes BEFORE `up -d`.

## Impact

- ✅ **Fixed**: Slicer worker deployment now works
- ✅ **Fixed**: Profile activation (OrcaSlicer, PrusaSlicer)
- ✅ **Fixed**: Both monolithic and microservices architectures
- ✅ **Fixed**: Scaling commands also updated to use correct command array

## Related Changes

Also updated scaling commands to use the same `final_compose_cmd` array:

```bash
# Scaling now uses the correct command with profiles
"${final_compose_cmd[@]}" up -d --scale orcaslicer-worker=2
"${final_compose_cmd[@]}" up -d --scale prusaslicer-worker=3
```

## Additional Notes

This is a common mistake when building Docker Compose commands dynamically in shell scripts. The key insight is that global flags (like `--profile`, `--env-file`, `-f`) must be added to the base command BEFORE adding the subcommand.

**Command building pattern:**
1. Start with base: `docker compose`
2. Add global options: `--env-file`, `-f`, `--profile`
3. Add subcommand: `up`
4. Add subcommand options: `-d`, `--scale`

## References

- Docker Compose CLI Reference: https://docs.docker.com/compose/reference/
- Using Profiles: https://docs.docker.com/compose/profiles/
