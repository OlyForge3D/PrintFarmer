---
name: "Refit Certificate Upgrade"
description: "Upgrade PrintFarmer off revoked Refit 10.1.6 packages and validate whether Refit 11 is safe."
domain: "dependency-management"
confidence: "high"
source: "earned from issue #497 on 2026-06-03T10:26:17.641-07:00"
---

## Context

Use this when CI or local restore fails because `Refit` or `Refit.HttpClientFactory` `10.1.6` has a revoked signing certificate (`NU3012`).

PrintFarmer keeps `Refit` in the repo-root `Directory.Build.props`, while `Refit.HttpClientFactory` is referenced from specific backend and service projects under `src/`.

## Pattern

1. Confirm the current package layout before editing:
   - `Directory.Build.props` carries `Refit`
   - `src/backends/Directory.Build.props` carries shared backend `Refit.HttpClientFactory`
   - Additional live `Refit.HttpClientFactory` references exist in `src/api/Farm.Web.Api.csproj`, `src/infra/Farm.Infrastructure.csproj`, `src/printer-discovery/PrinterDiscoveryService.csproj`, `src/slicer/Farm.Slicer.Module/Farm.Slicer.Module.csproj`, `src/migrations/Farm.Migrations.PostgreSQL/Farm.Migrations.PostgreSQL.csproj`, and `src/migrations/Farm.Migrations.SqlServer/Farm.Migrations.SqlServer.csproj`
2. Ignore `.tmp-pr443/` copies entirely.
3. Refit 11.0.0 is the latest stable release and still ships `Refit.HttpClientFactory` as a separate package. Update both packages to `11.0.0` unless the task explicitly prefers the non-breaking line.
4. If the goal is only to remove the revoked certificate with minimum behavior change, use `10.2.0`; upstream documents it as the re-signed replacement for `10.1.6`.
5. Validate from `src/` with:
   ```bash
   dotnet restore ./farm-web.sln
   dotnet build ./farm-web.sln -c Debug
   dotnet test ./farm-web.sln -c Debug
   ```

## Gotchas

- Refit 11 changes the error model (`ApiExceptionBase`, `ApiRequestException`, nullable `StatusCode`), so inspect code for direct `ApiResponse.Error`, `StatusCode`, or exception-type assumptions if the build breaks.
- In this repo, the upgrade built cleanly; the observed failing tests were pre-existing and unrelated to Refit:
  - `Farm.Slicer.Module.Tests.Slicers.OrcaSlicerProfilesProviderTests.*` failing on missing filament fixture JSON
  - `Farm.Web.Api.Tests.Integration.SystemInfoIntegrationTests.*` failing because `SlicerDbContext` is not registered in the test host
  - `Farm.Web.Api.Tests.Services.Printers.MmuToolheadRetroSyncTests.EnsureMmuToolheadsAsync_CreatesGates_ForLegacyMmuPrinter` failing on a count expectation mismatch
- Remove scratch validation logs before committing.
