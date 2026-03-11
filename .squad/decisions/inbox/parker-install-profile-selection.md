# Decision: Deployment Profile Selection in install.sh

**Author:** Parker (DevOps & Deployment Engineer)
**Date:** 2026-03-12
**Status:** IMPLEMENTED

## Problem

The install script had a single deployment path (3-container microservices). Users on Raspberry Pi needed a lighter option, and power users wanted monitoring + discovery included out of the box.

## Solution

Added `--profile lite|standard|full` flag with interactive menu fallback. Three deployment tiers mapped to different compose configurations.

## Key Decisions

1. **Lite forces SQLite** — no database container, no nginx, single monolith process on port 5000
2. **Full defaults to PostgreSQL** — but respects explicit `--db sqlite` override
3. **ARM auto-defaults to lite** — both interactive (pre-selected option 1) and non-interactive modes
4. **Profile stored in .env** — so future `--upgrade` runs know the active profile
5. **Inline compose generation** — all templates are generated directly in install.sh (no repo dependency)
6. **Backward compatible** — no `--profile` in non-interactive mode defaults to `standard` on non-ARM

## Impact

- **Lambert:** No API changes needed. Monolith `DEPLOYMENT_MODE=monolith` env var already wired.
- **Quinn:** No frontend changes. Profile is infrastructure-only.
- **Dallas:** Full profile adds discovery + monitoring services. Matches the 3-tier architecture from Pi analysis.
