# Decision: Installer Architecture Redesign

**Author:** Parker (DevOps)
**Date:** 2026-03-06
**Status:** Implemented

## Context

The existing `install.sh` (497 lines) was functional but had gaps for true beginners: no OS detection, no Docker installation guidance (just a hard fail), macOS-incompatible bash patterns (`grep -oP`, `${var,,}`), and hardcoded PostgreSQL (requires external DB knowledge).

## Decision

Rewrote the installer (now ~1030 lines) with these architectural choices:

1. **SQLite by default** — Zero-config database. Beginners don't need to understand PostgreSQL. Postgres available via `--db postgres` for those who want it.

2. **Bash 3.2 compatible** — macOS ships bash 3.2 (2007). All `${var,,}` lowercase expansions replaced with `tr`-based `lc()` helper. No PCRE grep. No associative arrays.

3. **Progressive Docker assistance** — Instead of dying on missing Docker, the installer detects OS/distro and offers platform-specific install instructions. On Debian/RHEL, offers auto-install via `get.docker.com`. On macOS, directs to Docker Desktop.

4. **Management helper script** — Generates `printfarmer.sh` alongside the compose file, giving beginners discoverable commands (`./printfarmer.sh logs`, `./printfarmer.sh backup`) without needing to learn docker compose syntax.

5. **Lifecycle commands** — Added `--upgrade`, `--uninstall`, `--status` as top-level flags. Users shouldn't need to read Docker docs to manage their install.

6. **LAN IP detection** — Post-install shows both localhost and LAN URLs, so users on a Raspberry Pi can immediately access from another device.

7. **Port conflict detection** — Checks if the port is in use before generating config, offers alternatives interactively.

## Alternatives Considered

- **Keep PostgreSQL default**: Rejected. SQLite is perfect for single-node self-hosted use (which is 90%+ of our users). No external service to configure or break.
- **Python/Node installer**: Rejected. Adds a prerequisite. Bash is universally available.
- **Docker-in-Docker installer**: Rejected. Over-engineered for the audience.

## Impact

- `install.sh` at repo root — replaces previous version
- No other files changed
- Fully backward compatible (all previous flags still work)
