# Project Context

- **Owner:** Jeff Papiez
- **Project:** PrintFarmer — React TypeScript dashboard for managing multiple 3D printers
- **Stack:** C# .NET 10 (API), React 19 TypeScript (Frontend), ASP.NET Core, EF Core, SignalR, Tailwind CSS, xUnit, Vitest
- **Deployment:** Docker Compose (multi-stage build), Nginx reverse proxy, multi-database support (SQLite, PostgreSQL, SQL Server, MySQL)
- **CI/CD:** GitHub Actions
- **Created:** 2026-03-06

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->
- Deploy script: `./scripts/deploy-docker.sh --non-interactive --no-cache` (from repo root)
- Dockerfile source of truth: `scripts/docker/dockerfiles/Dockerfile.multistage`
- Compose templates: `scripts/docker/compose-templates/`
- Root docker-compose.yml and Dockerfile.multistage are gitignored generated artifacts
- Installer (`install.sh`) is self-contained — generates compose + .env + nginx + management script for end users who don't clone the repo
- macOS bash is 3.2 — never use `${var,,}`, associative arrays, or `grep -oP`; use `tr`, indexed arrays, and `sed` instead
- Installer defaults to SQLite (zero config) with `--db postgres` opt-in for power users
- `printfarmer.sh` management helper is generated alongside the compose file for beginner-friendly lifecycle commands
- Container image registry: `ghcr.io/jpapiez/printfarmer-{api,frontend}:TAG`
- LAN IP detection: `hostname -I` on Linux, `ifconfig` on macOS, `ip route` as fallback
