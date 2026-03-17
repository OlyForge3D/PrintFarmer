# Decision: Docker publish workflow triggers for release branch

**Date:** 2025-07-25
**Author:** Lambert (Backend Dev)

## Context
The team needs Docker images built automatically when code is pushed to the `release` branch, matching the existing behavior for `main`.

## Decision
- `docker-publish.yml` now triggers on pushes to both `main` and `release` branches.
- Release branch pushes produce two tags: `release` (mutable, always points to latest release push) and `release-sha-{short}` (immutable, tied to specific commit).
- `containers.yml` was **not** modified. It's a scheduled optimization pipeline (daily + manual) that builds .NET/React natively then packages thin images. Its purpose is cache warming and base image freshness, not release gating. Adding push triggers there would duplicate the work `docker-publish.yml` already does on push events.

## Consequences
- Any push to `release` now builds and publishes all three images (api, frontend, monolith) to GHCR with `release` and `release-sha-*` tags.
- Teams can pull `printfarmer-api:release` for the latest release candidate, or pin to a specific `release-sha-*` tag for reproducibility.
