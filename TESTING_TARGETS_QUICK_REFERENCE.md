# Testing Targets Quick Reference

**Analysis Date:** December 8, 2025  
**Current Coverage:** 34.41% method (Farm.Web.Api: 28.71% line)  
**1-Page Summary of High-Impact, Low-Effort Testing Targets**

---

## 🎯 Phase 1 Quick Wins (4 Weeks, +1.0-1.2% Coverage, 70-80 Tests)

| Rank | Service | LOC | Tests | Effort | Gain | Hours | Priority |
|------|---------|-----|-------|--------|------|-------|----------|
| 1 | **PrusaLinkPollingService** | 296 | 10 | Low-Med | +0.35% | 7-10 | ⭐ |
| 2 | **ThumbnailGenerationService** | 291 | 10 | Low-Med | +0.34% | 7-10 | ⭐ |
| 3 | **OctoPrintPollingService** | 392 | 15 | Medium | +0.46% | 15-22 | ⭐⭐ |
| 4 | **HarvestWorkerService** | 618 | 20 | Medium | +0.73% | 20-30 | ⭐⭐ |
| 5 | **PrinterCapabilityDiscoveryService** | 697 | 15 | Medium | +0.82% | 15-25 | ⭐⭐ |
| 6 | **AssetsController** | 129 | 8 | Low | +0.19% | 5-7 | ⭐ |

**Phase 1 Total:** 70 tests, 80-100 hours, **35.4-35.6% coverage**

---

## 📊 Phase 2 Medium Targets (8 Weeks, +1.8-2.2% Coverage)

| Service | LOC | Tests | Priority | Effort | Gain |
|---------|-----|-------|----------|--------|------|
| GcodeHarvestService | 1510 | 25 | HIGH | High | +1.80% |
| DatabaseInitializer | 762 | 12 | MEDIUM | Med | +0.90% |
| SpoolmanController | 173 | 10 | MEDIUM | Low-Med | +0.26% |
| JobQueueController | 154 | 12 | MEDIUM | Low-Med | +0.23% |
| CatalogController | 217 | 12 | MEDIUM | Low-Med | +0.32% |
| GcodeLibraryController | 199 | 15 | MEDIUM | Medium | +0.30% |
| FileConsistencyController | 270 | 15 | MEDIUM | Medium | +0.40% |
| PrinterCapabilitiesController | 255 | 15 | MEDIUM | Medium | +0.38% |

**Phase 2 Total:** 116 tests, 150-180 hours, **37.2-37.8% coverage**

---

## 🚀 Phase 3 Critical Services (12 Weeks, +1.2-1.5% Coverage)

| Service | LOC | Tests | Complexity | Refs | Status |
|---------|-----|-------|------------|------|--------|
| GcodeHarvestService | 1510 | 25 | High | 11 | Critical pipeline |
| MoonrakerSubscriptionService | 1601 | 25 | High | 3 | Large but isolated |
| PrusaLinkClient | 673 | 25 | High | 44 | Already 27 test refs |
| SdcpClient | 864 | 20 | High | 41 | Already 32 test refs |

**Phase 3 Total:** 95 tests, 200-250 hours, **38.4-39.3% coverage**

---

## 📈 Long-Term Roadmap to 50%

| Milestone | Coverage | Tests | Timeline | Hours |
|-----------|----------|-------|----------|-------|
| Current | 34.41% | 1,134 | — | — |
| After Phase 1 | 35.41% | 1,204 | 4 weeks | 100 |
| After Phase 2 | 37.21% | 1,324 | 12 weeks | 250 |
| After Phase 3 | 38.41% | 1,419 | 24 weeks | 450 |
| After Phase 4 | 42.11% | 1,669 | 44 weeks | 700 |
| **TARGET: 50%** | **50.00%** | **~2,000** | **24-32 weeks** | **1,000+** |

---

## 🔍 All Untested Services (50 Total, 18,426 LOC)

### Extra Large (1000+ LOC) - Defer to Phase 3+
- **MoonrakerSubscriptionService** (1601) - 3 refs, Complex lifecycle
- **GcodeHarvestService** (1510) - 11 refs, Critical pipeline
- **OctoPrintClient** (1273) - 37 test refs (indirect testing)
- **SpoolmanService** (1118) - 37 test refs (well-tested already)
- **MoonrakerClient** (2063) - 73 test refs (well-tested already)

### Large (500-999 LOC) - Phase 2-3
- **SdcpClient** (864) - 41 refs, 32 test refs
- **DatabaseInitializer** (762) - 9 refs, 19 test refs
- **PrinterCapabilityDiscoveryService** (697) - 11 refs, 5 test refs
- **PrusaLinkClient** (673) - 44 refs, 27 test refs
- **HarvestWorkerService** (618) - 5 refs, 0 test refs

### Medium (200-499 LOC) - Phase 1-2
- **PrusaLinkApiClient** (579) - 7 refs
- **SlicersService** (524) - 4 refs
- **DatabaseSeeder** (512) - 5 refs
- **OrcaBundleExportService** (461) - 2 refs
- **SlicerOrchestrator** (454) - 0 refs

### Small (<200 LOC) - Phase 1 Quick Wins
- **OrcaBundleParsingService** (396)
- **FileConsistencyAuditService** (393)
- **OctoPrintPollingService** (392) ⭐
- **SlicerServiceMetrics** (349)
- **OrcaPresetMappingService** (344)
- **TagService** (339)
- **AuthAuditService** (303)
- **PrusaLinkPollingService** (296) ⭐
- **ThumbnailGenerationService** (291) ⭐
- **SliceJobEventService** (290)
- **[15 more under 200 LOC]**

---

## 📊 All Untested Controllers (22 Total, 7,803 LOC)

### Large (500+ LOC) - Low Priority
- **PrintersController** (1867) - HTTP routing, low ROI
- **GcodeFilesController** (892) - HTTP routing, low ROI

### Medium (200-499 LOC)
- **GcodeHarvestController** (402)
- **UnifiedSettingsController** (376)
- **AuthController** (367)
- **UsersController** (328)
- **ArtifactsController** (346) - *Has tests elsewhere*

### Small (<200 LOC) - Phase 1
- **FileConsistencyController** (270)
- **PrinterCapabilitiesController** (255)
- **CatalogController** (217)
- **GcodeLibraryController** (199)
- **SpoolmanController** (173)
- **JobQueueController** (154)
- **MoonrakerClientTestController** (141)
- **AssetsController** (129) ⭐
- **GcodeHarvestTestController** (105)
- **[7 more under 100 LOC]**

---

## 💡 Key Selection Criteria

### Why These 5 Services for Phase 1?

| Criterion | Importance | Why Phase 1 Winners |
|-----------|-----------|-------------------|
| **LOC** | High | <700 LOC (manageable scope) |
| **Complexity** | High | Medium-only (no High complexity) |
| **Test Effort** | High | 10-20 tests per service (fast) |
| **Coverage Gain** | High | +0.3-0.8% each (additive) |
| **Code Refs** | Medium | 5-11 references (used but focused) |
| **Established Patterns** | High | Polling/image generation (known domains) |

### Why Defer Large Services?

- **MoonrakerSubscriptionService** (1601 LOC) - Only 3 code references (isolated)
- **GcodeHarvestService** (1510 LOC) - 50-75 hours per service (high effort)
- **SdcpClient/PrusaLinkClient** - Already have 27-32 test references (indirect coverage)

---

## 🎬 Implementation Strategy

### Pattern-Based Testing
1. **Polling Services** (3 services): Create base test class
   - `PollingServiceTestBase<T>` with common test scenarios
   - Reduces duplication across PrusaLink, OctoPrint, Moonraker

2. **Image Processing** (1 service): Integration with file management
   - `ThumbnailGenerationService` tests
   - Mock image libraries, test format handling

3. **Status Discovery** (1 service): Similar patterns to status builders
   - `PrinterCapabilityDiscoveryService` tests
   - Reuse backend client mocks

### Recommended Test Library Stack
- **xUnit** - Already used in project
- **FluentAssertions** - Already used
- **Moq** - Mock dependencies
- **NSubstitute** - Alternative if needed

---

## 📈 Coverage Math

**Formula:** `new_tests × 0.009 ≈ method_coverage_increase`

| Tests Added | Expected Gain | New Coverage |
|------------|---------------|----------------|
| 10 | +0.09% | 34.50% |
| 50 | +0.45% | 34.86% |
| 70 | +0.63% | 35.04% |
| 100 | +0.90% | 35.31% |
| 200 | +1.80% | 36.21% |
| 300 | +2.70% | 37.11% |

---

## ✅ Success Criteria for Each Phase

### Phase 1 Success ✅
- [ ] All 70 tests passing
- [ ] Method coverage improves from 34.41% to 35.4%+ 
- [ ] No test flakiness or failures
- [ ] Test patterns established for Phase 2

### Phase 2 Success ✅
- [ ] 116 additional tests passing
- [ ] Method coverage reaches 37.2%+
- [ ] GcodeHarvestService well-tested
- [ ] Controller test suite expanded

### Phase 3 Success ✅
- [ ] 95 additional tests for large services
- [ ] Method coverage reaches 38.4%+
- [ ] Critical services fully tested
- [ ] Ready for Phase 4 optimization

---

## 📚 Related Documentation

See full analysis in:
- **`TESTING_ANALYSIS_HIGH_IMPACT_TARGETS.json`** - Complete JSON with all data
- **`TESTING_ANALYSIS_SUMMARY.md`** - Detailed phase-by-phase breakdown
- **`TEST_COVERAGE_IMPROVEMENT_PLAN.md`** - Historical improvements & architecture notes

---

**Next Steps:**
1. Review this quick reference
2. Read detailed summary (`.md` file)
3. Start Phase 1 with PrusaLinkPollingService
4. Create base polling test class
5. Run `dotnet test` weekly to track progress

**Questions?** See JSON file for detailed breakdown of every service/controller or coverage formulas.
