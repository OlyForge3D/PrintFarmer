# Decision: Standardize All API Controller Routes to Kebab-Case

**Date:** 2026-07-17
**Author:** Lambert (Backend Dev)
**Status:** Implemented

## Context

Several backend API controllers used inconsistent route patterns:
- Some used `[Route("api/[controller]")]` which resolves to PascalCase (e.g., `/api/JobScheduling`)
- Some used concatenated lowercase (e.g., `/api/autoprint`, `/api/systemlogs`)
- The frontend `api.ts` was already calling kebab-case URLs like `/auto-print` and `/system-logs`

## Decision

All API controller routes now use explicit kebab-case strings instead of the `[controller]` convention. Brand names (e.g., `filaman`) are left unchanged.

## Controllers Changed

| Controller | Before | After |
|---|---|---|
| AutoPrintController | `api/autoprint` | `api/auto-print` |
| SystemLogsController | `api/systemlogs` | `api/system-logs` |
| JobSchedulingController | `api/[controller]` | `api/job-scheduling` |
| PrintApprovalsController | `api/[controller]` | `api/print-approvals` |
| RetriesController | `api/[controller]` | `api/retries` |
| TasksController | `api/[controller]` | `api/tasks` |
| AssetsController | `api/[controller]` | `api/assets` |
| ArtifactsController | `api/[controller]` | `api/artifacts` |
| FileConsistencyController | `api/[controller]` | `api/file-consistency` |
| SlicersController | `api/[controller]` | `api/slicers` |
| WorkersController | `api/[controller]` | `api/workers` |

## Rule Going Forward

All new controllers MUST use explicit kebab-case `[Route("api/my-resource")]` — never `[Route("api/[controller]")]`.
