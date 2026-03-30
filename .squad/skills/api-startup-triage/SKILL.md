# API Startup Triage

Use this when the PrintFarmer API is reported as "container won't start" but it is not yet clear whether the failure is image/runtime, environment wiring, or backend application startup.

## Procedure

1. Run `docker compose config` first and confirm the resolved API service actually has:
   - `ConnectionStrings__Default`
   - `DB_PROVIDER`
   - `Jwt__Key`
   - expected internal port `5245`
2. Check whether the local `printfarmer-api` image exists before assuming an app crash.
   - If the image is missing, `docker compose up api` may try to pull instead of creating a container.
   - That is an infra/runtime problem, not proof of a backend startup regression.
3. If the database image is available, start only the database container and validate the backend separately with a local `dotnet run`.
   - Reuse compose-equivalent environment values.
   - Look for `[Startup] Step 1/3`, `Step 2/3`, `Step 3/3`, and `Database initialization complete`.
4. Separate non-fatal startup noise from the real blocker.
   - Early missing-table queries against `AppSettingsEntities` / `SystemLogs` can appear before schema initialization.
   - They are worth fixing later, but they are not automatically the reason the container failed.
5. Watch for local-only validation traps.
   - `Program.cs` currently forces port `5245`, so local `ASPNETCORE_URLS` overrides do not win.
   - A local `address already in use` error is not evidence of a Docker startup failure when the container also expects `5245`.

## Output

Report one of these clearly:

- **Infra/runtime issue** — image missing, compose pull/build problem, container never reached app startup
- **Backend startup issue** — concrete app exception or fatal startup log
- **No backend regression reproduced** — backend startup completed under compose-equivalent settings
