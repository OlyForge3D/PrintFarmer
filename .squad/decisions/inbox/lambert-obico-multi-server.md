# Decision: Obico Server API Key — Write-Only Security Pattern

**Author:** Lambert (Backend Dev)
**Date:** 2026-03-17
**Status:** IMPLEMENTED

## Context

Obico ML servers can be self-hosted (no auth) or cloud/secured (requires API key). The existing multi-server ObicoServer entity had no authentication field.

## Decision

Added optional `ApiKey` field to `ObicoServer` with a **write-only security pattern**:

- **API Response:** Returns `hasApiKey: true/false` — never exposes the actual key
- **Create/Update:** Accepts `apiKey` string to set/update the key
- **Clear Key:** Send empty string `""` in update to remove the key
- **Auth Method:** Sent as `Authorization: Bearer <key>` header on all Obico API requests

## Rationale

- API keys are secrets — exposing them in GET responses would be a security risk
- The `hasApiKey` boolean lets the UI show whether auth is configured without leaking the value
- Bearer token is the standard auth mechanism for HTTP APIs
- Nullable field ensures backward compatibility — existing servers without keys continue working

## Impact

- **Entity:** `ObicoServer.ApiKey` (nullable, max 500 chars)
- **Services:** `IObicoFailureDetectionService` and `PrintFailureMonitorService` pass API key through
- **Migrations:** Both PostgreSQL and SqlServer — simple `AddColumn` (nullable, no data loss)
- **Frontend:** `ObicoServer` type gains `hasApiKey` boolean, create/update types gain `apiKey`

## Team Impact

- **Ripley (Frontend):** Update ObicoServersSection to include optional API key field in create/edit forms. Show "API key configured" badge when `hasApiKey` is true.
- **Parker (DevOps):** No infrastructure changes needed — API key is per-server configuration.
