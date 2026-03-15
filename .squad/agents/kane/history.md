## Learnings

### Camera Management Phase 1.5 Testing (2026-01-11)
- **Test Created**: `src/tests/Farm.Web.Api.Tests/Controllers/CameraManagementTests.cs` with 12 comprehensive integration tests
- **Key Patterns Learned**:
  - Use `CustomWebApplicationFactory` for integration tests with in-memory SQLite
  - Use `AppDbContext` (not `FarmDbContext`) for database access
  - Printer entity requires `ManufacturerId` and `ModelId` - create defaults if needed
  - Printer's `ServerUrl` has a unique constraint - use unique GUID-based URLs for test printers
  - JSON enum serialization requires `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)` for camelCase APIs
  - Use `_jsonOptions` with custom settings when deserializing HTTP responses
  - FluentAssertions uses `BeGreaterThanOrEqualTo()` not `HaveCountGreaterOrEqualTo()`
- **Test Coverage**: All 12 new camera management tests pass, plus all existing 2052 tests still pass (2064 total)
