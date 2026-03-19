# Decision: Unified Docker Workflow

**Author:** Parker (DevOps)
**Date:** 2026-03-17
**Status:** Implemented

## Context

We had two overlapping Docker CI/CD workflows:
- `docker-publish.yml` — release pipeline using Dockerfile.multistage, comprehensive tagging, triggers on push/tags/manual
- `containers.yml` — optimized pipeline using native build on runner + COPY into minimal containers, daily schedule + manual only

Both built api and frontend images. containers.yml additionally built printer-discovery and orcaslicer-worker. docker-publish.yml additionally built monolith. Maintaining two workflows with different build strategies, triggers, and tagging was confusing and error-prone.

## Decision

Unified into a single `docker-publish.yml` workflow that takes the best of both:

1. **Build strategy:** Native build (from containers.yml) for api, frontend, printer-discovery, orcaslicer-worker. Multistage Dockerfile (from docker-publish.yml) for monolith only — it can't use native build since it combines API + frontend in one image.

2. **Triggers:** Combined — push to main/release, version tags, daily schedule, manual dispatch with tag_suffix input.

3. **Tagging:** Comprehensive (from docker-publish.yml) — semver, branch names, SHA prefixes, release-specific tags, manual tags, nightly schedule tags. Applied uniformly to all 5 images.

4. **All 5 images in one pipeline:** api, frontend, printer-discovery, orcaslicer-worker, monolith.

5. **Monolith runs in parallel** with native-build containers — no dependency on build-dotnet/build-frontend jobs.

## Consequences

- **For the team:** One workflow to monitor, one place to update triggers/tagging/build logic.
- **For builds:** Native build path is faster with better caching for 4 of 5 images. Monolith retains multistage build since it's architecturally different.
- **For releases:** All 5 images get identical tagging treatment — semver tags on version pushes, SHA tags on branch pushes, nightly tags on schedule.
- **Deleted:** `containers.yml` is gone. Any references to it should be updated.

## Affected Components

- `.github/workflows/docker-publish.yml` — replaced contents
- `.github/workflows/containers.yml` — deleted
