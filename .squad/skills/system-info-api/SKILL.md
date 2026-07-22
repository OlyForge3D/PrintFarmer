## System Info API Pattern

Use this pattern when adding or extending `GET /api/system/info`-style diagnostics in PrintFarmer.

### Contract placement

- Put the response DTOs in `src/infra/Dtos/`.
- Keep the controller in `src/api/Controllers/` thin; inject an infra service interface.
- Register the service in `src/api/Startup/FeatureServicesStartup.cs`.

### Data source pattern

- **App version:** reuse the assembly informational version logic already used by `/api/system/version`.
- **CPU:** Linux = `/proc/stat`; Windows = `GetSystemTimes`; short two-sample delay (~150 ms).
- **Memory:** Linux = `/proc/meminfo`; Windows = `GlobalMemoryStatusEx`; fallback = `Process.WorkingSet64`.
- **Disk/archive:** use `IStoragePathService.GetGcodeStorageDirectory()`; walk the directory tree defensively.
- **Database engine/version/size:** branch on `AppDbContext.Database.ProviderName`; use provider-specific scalar SQL.

### Safety rules

- Do not fail the endpoint because one metric source is unavailable; degrade to `0`, `Unknown`, or a documented fallback.
- Keep auth at the controller boundary with `farm_admin`.
- Cover 401, 403, shape, and enum-string serialization in integration tests under `src/tests/Farm.Web.Api.Tests/Integration/`.
