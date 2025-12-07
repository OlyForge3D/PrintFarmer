# Test Coverage Improvement Plan - Critical Paths

## Current Status (as of 2025-12-07)

**Coverage Summary:**
- **Farm.Web.Api**: 23.01% line coverage, 18.79% branch coverage
- **Farm.Infrastructure**: 30.67% line coverage, 15.43% branch coverage
- **Overall Average**: 23.98% line coverage, 18% branch coverage
- **Total Tests**: 496 passing, 0 skipped, 0 failures

**Priority**: Increase coverage to **60%+ line coverage** focusing on critical business paths

---

## Phase 1: Critical Business Logic (Priority: 🔴 HIGHEST)

### 1.1 Printer Management (`PrintersService.cs`)
**Current Coverage**: Unknown (likely <30%)  
**Target**: 80%+ coverage

**Critical Paths to Test:**
- ✅ Printer CRUD operations (Create, Read, Update, Delete)
- ⚠️ **MISSING**: Printer status updates and state management
- ⚠️ **MISSING**: Printer capability detection and updates
- ⚠️ **MISSING**: Printer heartbeat handling
- ⚠️ **MISSING**: Network connectivity validation
- ⚠️ **MISSING**: Multi-printer coordination logic

**Test Files Needed:**
- `PrintersServiceTests.cs` - Unit tests for business logic
- `PrintersControllerIntegrationTests.cs` - End-to-end API tests
- `PrinterStateManagementTests.cs` - State transition tests

**Test Scenarios:**
```csharp
// 1. Printer registration with valid configuration
// 2. Printer status update via Moonraker/PrusaLink
// 3. Printer offline detection and recovery
// 4. Concurrent printer status updates
// 5. Invalid printer configuration rejection
// 6. Printer deletion with active jobs
// 7. Printer capability auto-detection
```

---

### 1.2 Job Queue Management (`JobQueueService.cs`)
**Current Coverage**: 0% (evident from coverage report)  
**Target**: 85%+ coverage

**Critical Paths to Test:**
- ⚠️ **MISSING**: Add job to queue with priority
- ⚠️ **MISSING**: Update job status (queued → assigned → printing → completed/failed)
- ⚠️ **MISSING**: Job cancellation and cleanup
- ⚠️ **MISSING**: Printer assignment logic
- ⚠️ **MISSING**: Queue ordering by priority
- ⚠️ **MISSING**: Concurrent queue operations
- ⚠️ **MISSING**: Job timeout handling

**Test Files Needed:**
- `JobQueueServiceTests.cs` - Core queue logic tests
- `JobQueueIntegrationTests.cs` - Full workflow tests
- `JobPriorityTests.cs` - Priority and ordering tests

**Test Scenarios:**
```csharp
// 1. Queue job with high priority - verify placement at front
// 2. Queue multiple jobs - verify FIFO for same priority
// 3. Update job status through lifecycle
// 4. Cancel in-progress job - verify printer notification
// 5. Delete queued job - verify no side effects
// 6. Assign job to available printer - verify printer receives job
// 7. Handle printer failure during job - verify retry/failure logic
// 8. Concurrent job additions - verify no race conditions
```

---

### 1.3 File Upload & Management (`ChunkedUploadService.cs`, `GcodeFilesService.cs`)
**Current Coverage**: Unknown (likely <25%)  
**Target**: 75%+ coverage

**Critical Paths to Test:**
- ✅ Basic file upload (single file)
- ⚠️ **MISSING**: Chunked upload initialization
- ⚠️ **MISSING**: Chunk append and validation
- ⚠️ **MISSING**: Upload completion and finalization
- ⚠️ **MISSING**: Upload pause/resume functionality
- ⚠️ **MISSING**: File integrity verification (hash validation)
- ⚠️ **MISSING**: Thumbnail extraction from G-code
- ⚠️ **MISSING**: Metadata extraction and storage
- ⚠️ **MISSING**: File quota enforcement
- ⚠️ **MISSING**: Orphaned file cleanup

**Test Files Needed:**
- `ChunkedUploadServiceTests.cs` - Upload mechanism tests
- `GcodeFilesServiceTests.cs` - File management tests
- `FileIntegrityTests.cs` - Hash validation and integrity tests

**Test Scenarios:**
```csharp
// 1. Upload small file (< chunk size) - single operation
// 2. Upload large file (> chunk size) - verify chunking
// 3. Upload with invalid chunk offset - verify rejection
// 4. Upload exceeding quota - verify rejection
// 5. Resume interrupted upload - verify continuation
// 6. Finalize upload - verify metadata extraction
// 7. Extract thumbnail from G-code - verify PNG creation
// 8. Verify file hash - detect corruption
// 9. Delete file - verify cleanup of thumbnails and DB entries
```

---

### 1.4 Slicing Job Submission (`SlicingSubmissionService.cs`)
**Current Coverage**: Unknown (likely 0-15%)  
**Target**: 80%+ coverage

**Critical Paths to Test:**
- ⚠️ **MISSING**: Submit slicing job from uploaded model
- ⚠️ **MISSING**: Submit slicing job from stored model
- ⚠️ **MISSING**: Profile selection and validation
- ⚠️ **MISSING**: Model file validation before slicing
- ⚠️ **MISSING**: Slicer engine selection (OrcaSlicer/PrusaSlicer)
- ⚠️ **MISSING**: Job parameter validation
- ⚠️ **MISSING**: Job submission failure handling
- ⚠️ **MISSING**: Worker assignment for slicing jobs

**Test Files Needed:**
- `SlicingSubmissionServiceTests.cs` - Job submission tests
- `SlicingIntegrationTests.cs` - End-to-end slicing tests
- `ProfileValidationTests.cs` - Profile compatibility tests

**Test Scenarios:**
```csharp
// 1. Submit job with valid model and profile - verify job created
// 2. Submit job with nonexistent model - verify error
// 3. Submit job with incompatible profile - verify rejection
// 4. Submit job when no workers available - verify queuing
// 5. Submit multiple jobs concurrently - verify worker assignment
// 6. Submit job with custom parameters - verify override
// 7. Cancel slicing job mid-process - verify cleanup
```

---

## Phase 2: Authentication & Authorization (Priority: 🟠 HIGH)

### 2.1 Authentication Service (`AuthService`, `AuthController`)
**Current Coverage**: ~40% (some tests exist)  
**Target**: 90%+ coverage

**Critical Paths to Test:**
- ✅ User login with valid credentials
- ✅ User registration
- ⚠️ **MISSING**: Password hashing security
- ⚠️ **MISSING**: Token generation and validation
- ⚠️ **MISSING**: Token refresh logic
- ⚠️ **MISSING**: Token revocation
- ⚠️ **MISSING**: Account lockout after failed attempts
- ⚠️ **MISSING**: Password policy enforcement
- ⚠️ **MISSING**: Session management

**Test Files Needed:**
- `AuthenticationTests.cs` - Core auth logic
- `TokenManagementTests.cs` - JWT token tests
- `PasswordSecurityTests.cs` - Password policy and hashing

---

### 2.2 Authorization & RBAC
**Current Coverage**: Low (factory always authenticates in tests)  
**Target**: 75%+ coverage

**Critical Paths to Test:**
- ⚠️ **MISSING**: Role-based access control (Admin, User)
- ⚠️ **MISSING**: Endpoint authorization enforcement
- ⚠️ **MISSING**: Resource ownership validation
- ⚠️ **MISSING**: Cross-user data isolation

**Test Files Needed:**
- `AuthorizationTests.cs` - RBAC tests
- `DataIsolationTests.cs` - Multi-user isolation tests

---

## Phase 3: External Integrations (Priority: 🟡 MEDIUM)

### 3.1 Moonraker Client (`MoonrakerClient.cs`)
**Current Coverage**: ~20% (basic tests exist)  
**Target**: 70%+ coverage

**Critical Paths to Test:**
- ✅ Basic printer status retrieval
- ⚠️ **MISSING**: Print job start/stop/pause
- ⚠️ **MISSING**: File upload to printer
- ⚠️ **MISSING**: Webcam stream access
- ⚠️ **MISSING**: Temperature monitoring
- ⚠️ **MISSING**: Network error handling and retry
- ⚠️ **MISSING**: WebSocket subscription management

**Test Files Needed:**
- `MoonrakerClientTests.cs` - API client tests with mocked responses
- `MoonrakerIntegrationTests.cs` - Real API integration tests (optional)

---

### 3.2 PrusaLink Client (`PrusaLinkClient.cs`)
**Current Coverage**: ~15%  
**Target**: 70%+ coverage

**Critical Paths to Test:**
- ⚠️ **MISSING**: Printer status retrieval
- ⚠️ **MISSING**: Job operations (start, stop, pause)
- ⚠️ **MISSING**: File operations
- ⚠️ **MISSING**: API version compatibility

**Test Files Needed:**
- `PrusaLinkClientTests.cs` - API client tests

---

## Phase 4: Background Services (Priority: 🟡 MEDIUM)

### 4.1 File Consistency Audit (`FileConsistencyAuditService.cs`)
**Current Coverage**: ~60% (good integration tests)  
**Target**: 85%+ coverage

**Critical Paths to Test:**
- ✅ Health check summary
- ✅ Audit history retrieval
- ⚠️ **MISSING**: Background audit execution
- ⚠️ **MISSING**: Missing file detection
- ⚠️ **MISSING**: Corrupted file detection
- ⚠️ **MISSING**: Orphaned file cleanup

---

### 4.2 Worker Health Monitoring (`WorkerHealthMonitorService.cs`)
**Current Coverage**: Unknown (likely 0%)  
**Target**: 75%+ coverage

**Critical Paths to Test:**
- ⚠️ **MISSING**: Worker heartbeat timeout detection
- ⚠️ **MISSING**: Worker status updates (online → offline)
- ⚠️ **MISSING**: Job reassignment on worker failure
- ⚠️ **MISSING**: Worker recovery after downtime

**Test Files Needed:**
- `WorkerHealthMonitorTests.cs` - Health monitoring logic
- `WorkerFailoverTests.cs` - Failure and recovery scenarios

---

## Phase 5: SignalR Real-time Updates (Priority: 🟢 LOW)

### 5.1 SignalR Hubs (`PrinterHub`, `SlicerProgressHub`)
**Current Coverage**: ~50% (health checks exist)  
**Target**: 70%+ coverage

**Critical Paths to Test:**
- ✅ SignalR hub connection
- ⚠️ **MISSING**: Real-time printer status broadcasts
- ⚠️ **MISSING**: Slicing progress updates
- ⚠️ **MISSING**: Job queue updates
- ⚠️ **MISSING**: Connection handling (reconnect, timeout)

**Test Files Needed:**
- `SignalRHubTests.cs` - Hub method tests
- `RealTimeUpdatesIntegrationTests.cs` - End-to-end SignalR tests

---

## Implementation Strategy

### Step 1: Setup Test Infrastructure (Week 1)
1. ✅ Verify xUnit test runner configured
2. ✅ Verify FluentAssertions available
3. ⚠️ Add Moq for mocking dependencies
4. ⚠️ Create test fixtures for common scenarios
5. ⚠️ Setup test data builders for domain models

### Step 2: Priority 1 - Critical Business Logic (Weeks 2-4)
**Order of Implementation:**
1. **JobQueueService** (3 days) - Most critical, currently 0% coverage
2. **PrintersService** (4 days) - Core printer management
3. **ChunkedUploadService** (3 days) - File upload critical path
4. **SlicingSubmissionService** (3 days) - Slicing workflow
5. **GcodeFilesService** (2 days) - File management

**Target**: +40% overall coverage (from 24% → 64%)

### Step 3: Priority 2 - Authentication (Week 5)
1. **AuthenticationTests** (2 days)
2. **TokenManagementTests** (2 days)
3. **AuthorizationTests** (1 day)

**Target**: +5% overall coverage (from 64% → 69%)

### Step 4: Priority 3 - External Integrations (Week 6)
1. **MoonrakerClientTests** (2 days)
2. **PrusaLinkClientTests** (2 days)
3. **Network resilience tests** (1 day)

**Target**: +5% overall coverage (from 69% → 74%)

### Step 5: Priority 4 - Background Services (Week 7)
1. **WorkerHealthMonitorTests** (2 days)
2. **FileConsistencyAuditService enhancements** (1 day)
3. **Job timeout scanner tests** (1 day)

**Target**: +3% overall coverage (from 74% → 77%)

---

## Test Writing Guidelines

### 1. AAA Pattern (Arrange-Act-Assert)
```csharp
[Fact]
public async Task QueueJob_WithHighPriority_PlacesAtFrontOfQueue()
{
    // Arrange
    var service = CreateJobQueueService();
    await service.AddJobAsync(CreateJob(priority: 0)); // Normal priority
    var highPriorityJob = CreateJob(priority: 10);

    // Act
    await service.AddJobAsync(highPriorityJob);
    var queue = await service.GetQueueAsync();

    // Assert
    queue.First().Id.Should().Be(highPriorityJob.Id);
    queue.Should().HaveCount(2);
}
```

### 2. Test Naming Convention
```
MethodName_StateUnderTest_ExpectedBehavior
```
Examples:
- `AddJob_WithValidData_ReturnsCreatedJob`
- `UpdateJobStatus_WhenJobNotFound_ThrowsNotFoundException`
- `CancelJob_WhenJobInProgress_NotifiesPrinter`

### 3. Use Test Fixtures for Common Setup
```csharp
public class JobQueueServiceTestFixture : IAsyncLifetime
{
    public AppDbContext DbContext { get; private set; }
    public JobQueueService Service { get; private set; }
    
    public async Task InitializeAsync()
    {
        // Setup database, services, etc.
    }
    
    public async Task DisposeAsync()
    {
        // Cleanup
    }
}
```

### 4. Mock External Dependencies
```csharp
var mockPrinterClient = new Mock<IMoonrakerClient>();
mockPrinterClient
    .Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>()))
    .ReturnsAsync(new PrinterStatus { State = "ready" });
```

### 5. Test Data Builders
```csharp
public class PrintJobBuilder
{
    private Guid _id = Guid.NewGuid();
    private int _priority = 0;
    private JobStatus _status = JobStatus.Queued;
    
    public PrintJobBuilder WithPriority(int priority)
    {
        _priority = priority;
        return this;
    }
    
    public PrintJob Build() => new() { Id = _id, Priority = _priority, Status = _status };
}

// Usage
var job = new PrintJobBuilder().WithPriority(10).Build();
```

---

## Continuous Integration Requirements

### Pre-Commit Hooks
```bash
#!/bin/bash
# Run tests before allowing commit
dotnet test ./src/farm-web.sln -c Debug
if [ $? -ne 0 ]; then
    echo "Tests failed. Commit aborted."
    exit 1
fi
```

### CI Pipeline Requirements
1. Run all tests on every PR
2. Fail PR if coverage drops below current baseline
3. Generate coverage reports for review
4. Block merge if tests fail

### Coverage Goals by Milestone
- **Milestone 1** (End of Week 4): 64% line coverage
- **Milestone 2** (End of Week 5): 69% line coverage
- **Milestone 3** (End of Week 7): 77% line coverage
- **Final Goal** (End of Quarter): 80%+ line coverage

---

## Metrics & Tracking

### Weekly Coverage Report
```bash
# Generate coverage report
dotnet test ./src/farm-web.sln -c Debug --collect:"XPlat Code Coverage"

# View summary
cat ./src/tests/coverage/Farm.Web.Api.Tests.info | grep "LH:\|LF:"
```

### Coverage Dashboard
Track weekly progress:
| Week | Line Coverage | Branch Coverage | New Tests Added | Notes |
|------|---------------|-----------------|-----------------|-------|
| Week 0 (Baseline) | 23.98% | 18% | 496 | Current state |
| Week 1 | TBD | TBD | TBD | Infrastructure setup |
| Week 2-4 | Target: 64% | Target: 40% | ~100 | Phase 1 |
| Week 5 | Target: 69% | Target: 45% | ~30 | Phase 2 |
| Week 6 | Target: 74% | Target: 50% | ~30 | Phase 3 |
| Week 7 | Target: 77% | Target: 55% | ~20 | Phase 4 |

---

## Success Criteria

**Phase 1 Complete When:**
- ✅ JobQueueService has 85%+ coverage
- ✅ PrintersService has 80%+ coverage
- ✅ ChunkedUploadService has 75%+ coverage
- ✅ SlicingSubmissionService has 80%+ coverage
- ✅ All new tests pass consistently
- ✅ No regressions in existing tests
- ✅ Overall coverage reaches 64%+

**Final Success Criteria:**
- ✅ Overall line coverage ≥ 77%
- ✅ Branch coverage ≥ 55%
- ✅ Zero failing tests
- ✅ All critical business paths have dedicated tests
- ✅ CI pipeline enforces coverage requirements
- ✅ Documentation updated with test writing guidelines

---

## Files to Create

**Priority 1 (Critical Business Logic):**
1. `src/tests/Farm.Web.Api.Tests/Services/Queue/JobQueueServiceTests.cs`
2. `src/tests/Farm.Web.Api.Tests/Services/Queue/JobQueueIntegrationTests.cs`
3. `src/tests/Farm.Web.Api.Tests/Services/Printers/PrintersServiceTests.cs`
4. `src/tests/Farm.Web.Api.Tests/Services/Printers/PrinterStateManagementTests.cs`
5. `src/tests/Farm.Web.Api.Tests/Services/FileManagement/ChunkedUploadServiceTests.cs`
6. `src/tests/Farm.Web.Api.Tests/Services/FileManagement/FileIntegrityTests.cs`
7. `src/tests/Farm.Web.Api.Tests/Services/Slicing/SlicingSubmissionServiceTests.cs`
8. `src/tests/Farm.Web.Api.Tests/Services/Gcode/GcodeFilesServiceTests.cs`

**Priority 2 (Authentication):**
9. `src/tests/Farm.Web.Api.Tests/Services/Authentication/TokenManagementTests.cs`
10. `src/tests/Farm.Web.Api.Tests/Services/Authentication/PasswordSecurityTests.cs`
11. `src/tests/Farm.Web.Api.Tests/Services/Authentication/AuthorizationTests.cs`

**Priority 3 (External Integrations):**
12. `src/tests/Farm.Web.Api.Tests/Services/MoonrakerClientTests.cs`
13. `src/tests/Farm.Web.Api.Tests/Services/PrusaLinkClientTests.cs`

**Priority 4 (Background Services):**
14. `src/tests/Farm.Web.Api.Tests/Services/Workers/WorkerHealthMonitorTests.cs`
15. `src/tests/Farm.Web.Api.Tests/Services/Workers/WorkerFailoverTests.cs`

**Test Infrastructure:**
16. `src/tests/Farm.Web.Api.Tests/Builders/PrintJobBuilder.cs`
17. `src/tests/Farm.Web.Api.Tests/Builders/PrinterBuilder.cs`
18. `src/tests/Farm.Web.Api.Tests/Fixtures/JobQueueServiceTestFixture.cs`
19. `src/tests/Farm.Web.Api.Tests/Fixtures/PrintersServiceTestFixture.cs`

---

## Next Steps

1. **Review this plan** with the team
2. **Prioritize test files** - confirm Phase 1 order
3. **Setup test infrastructure** (Moq, fixtures, builders)
4. **Begin Phase 1** - Start with JobQueueService tests
5. **Track progress weekly** - Update coverage dashboard
6. **Iterate and adjust** - Revise plan based on findings

**Estimated Timeline**: 7 weeks to reach 77% coverage  
**Estimated Effort**: ~140 hours (20 hours/week)  
**ROI**: Significantly reduced regression risk, faster feature development, improved code quality
