# CorrelationId Propagation in PrintFarmer

## Overview
PrintFarmer implements end-to-end request tracing using a `correlationId` to link frontend, backend, and log entries. This enables robust debugging, monitoring, and auditability across the entire stack.

## How CorrelationId Works
- **Frontend**: The React client generates a unique `correlationId` for each API request and sends it as the `X-Correlation-Id` HTTP header.
- **Backend Middleware**:
  - **TelemetryMiddleware**: Extracts the `X-Correlation-Id` header from incoming requests. If missing, it falls back to `HttpContext.TraceIdentifier`. The value is stored in `HttpContext.Items["CorrelationId"]` for downstream access.
  - **GlobalExceptionMiddleware**: Reads the correlationId from `HttpContext.Items` and includes it in all error logs and structured error responses.
  - **SpaDynamicProxyMiddleware**: Propagates the correlationId to proxied SPA requests, ensuring traceability for frontend routes.
- **Logging**: All calls to `IUnifiedLoggingService` and `UnifiedLoggingService` include the correlationId, which is persisted to the `SystemLog` table and included in telemetry.

## Developer Usage
- **Frontend**: No manual action required; the API client automatically generates and attaches the correlationId.
- **Backend**: Access the correlationId in controllers/services via:
  ```csharp
  string correlationId = HttpContext.Items["CorrelationId"] as string ?? HttpContext.TraceIdentifier;
  ```
  Pass this value to logging calls for full traceability.

## Error Responses
All error responses from the API include the correlationId, allowing users and admins to correlate frontend errors with backend logs.

## Example Flow
1. React client sends API request with `X-Correlation-Id: <uuid>`
2. TelemetryMiddleware extracts and stores the correlationId
3. All logs and errors for the request include the correlationId
4. Admins can trace a user action from frontend to backend logs using the correlationId

## Benefits
- End-to-end traceability for debugging and auditing
- Easier correlation of frontend errors with backend logs
- Improved monitoring and diagnostics

## References
- `src/Web/ReactApp/src/services/api.ts` (frontend API client)
- `src/api/Middleware/TelemetryMiddleware.cs` (backend extraction)
- `src/api/Middleware/GlobalExceptionMiddleware.cs` (error handling)
- `src/api/Middleware/SpaDynamicProxy.cs` (SPA proxy propagation)
- `src/Farm.Infrastructure/Telemetry/UnifiedLoggingService.cs` (logging)
- `src/Farm.Infrastructure/Domain/Entities.cs` (SystemLog entity)
