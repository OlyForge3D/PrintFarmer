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

---

## Wave 1 Completion — Cross-Agent Updates

**2026-03-16 — POST-WAVE-1 INTEGRATION NOTES**

### Incoming Work (Wave 2)
- ✅ Five-Feature Workplan approved
- Feature #2 & #3 test suite responsibilities assigned to you
- **Feature #2 (PWA Notifications):** Ripley completed UI, you write notification workflows
- **Feature #3 (Cost Dashboard):** Lambert completed backend, you verify cost calculations and dashboard integration
- Full workplan: `.squad/decisions/inbox/dallas-five-features-workplan.md`

### Ready-to-Test Components
- Ripley: NotificationBell, NotificationDrawer components (WCAG 2.2 AA compliant)
- Lambert: 6 cost API endpoints, JobCostCalculationService, migrations
- Coordination: All 4 Wave 1 agents delivered clean builds (0 errors)

**Wave 2 Priority:** Comprehensive test suite for notifications + cost tracking
**Status:** Ready to begin test harness work
