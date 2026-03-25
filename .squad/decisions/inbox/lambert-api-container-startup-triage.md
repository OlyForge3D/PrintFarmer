## API Container Startup Triage

**Author:** Lambert (Backend Dev)  
**Date:** 2026-03-25

**Decision:** Do not change backend startup code for the current API-container report yet. The backend startup path was validated separately against Postgres and completed its database initialization sequence successfully.

**Why:** In this workspace, `docker compose up api` never produced a real application container to inspect because the `printfarmer-api` image was missing locally and Compose tried to pull it. That points to an infra/runtime problem first, not a confirmed application-startup regression.

**Notes for the team:**
- Compose-resolved API settings already include `ConnectionStrings__Default` and `Jwt__Key`, so the obvious backend config prerequisites are present in the generated runtime config.
- Startup still logs early `AppSettingsEntities` / `SystemLogs` missing-table errors before schema creation. Those are noisy and worth a separate cleanup pass, but they were non-fatal during validation.
- `Program.cs` currently forces `http://0.0.0.0:5245`, which makes local port-override validation harder. It is not the likely cause of a container failing on the expected internal port.
