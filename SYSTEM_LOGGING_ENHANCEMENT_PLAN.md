# System Logging Enhancement Implementation Plan

This plan introduces correlation IDs and arbitrary JSON metadata to all backend logging, ensures all logs are persisted to the `SystemLog` table, and enables full traceability and export. The work is divided into clear, incremental phases.

---

## Phase 1: Logging Interface & Model Extension

- [x] Extend `IUnifiedLoggingService` and `UnifiedLoggingService` to accept a `correlationId` and `metadata` (as a JSON-serializable object or string) on all logging methods.
- [x] Update the `SystemLog` entity and database table to include `CorrelationId` and `Metadata` (as a JSON column or string).
- [x] Add/Update EF Core migration for the new columns.

---

## Phase 2: Backend Logging Integration

- [x] Update all usages of the logger in backend code to pass the `correlationId` (from HTTP context/request or generated if missing).
- [x] Update all usages to optionally pass structured metadata (e.g., user info, request context, printer/job IDs).
- [x] Ensure all log levels and types are persisted to the `SystemLog` table, including the new fields.

---

## Phase 3: Frontend & API Correlation

- [x] Update frontend to generate and send a `correlationId` with every API request (e.g., in a header).
- [x] Update backend middleware to extract and propagate the `correlationId` for all requests and logs.
- [x] Document the correlation ID propagation for developers and admins. See `docs/CORRELATION_ID_PROPAGATION.md`.

**Phase 3 Completed:**
- The React frontend now generates and sends a unique correlationId with every API request.
- TelemetryMiddleware extracts the correlationId from incoming requests and stores it in HttpContext.Items.
- GlobalExceptionMiddleware and all logging calls use the correlationId for traceability.
- SpaDynamicProxyMiddleware propagates the correlationId for SPA requests.
- Documentation for correlationId propagation is available in `docs/CORRELATION_ID_PROPAGATION.md`.

---

## Phase 4: Log Query, Export, and Metadata Support

- [x] Update or add API endpoints to query and export logs, supporting filtering by `correlationId`, log level, date, and metadata fields.
- [x] Ensure exported logs include all metadata in JSON format.
- [x] Add UI support for searching and exporting logs by correlation ID and metadata.

**Phase 4 Completed:**
- Added `SystemLogsController` API endpoints for querying and exporting logs with filters for correlationId, level, date, and metadata.
- Exported logs include all metadata in JSON format.
- Created a new React admin page (`/admin/logs`) for searching, filtering, and exporting logs by correlationId and metadata.

---

## Phase 5: Testing, Validation, and Documentation

- [x] Add unit/integration tests for logging with correlation IDs and metadata.
- [x] Validate end-to-end traceability from UI to backend and back.
- [x] Update documentation for log retention, export, and debugging workflows.

---

**Progress will be tracked by checking off each item as it is completed.**
