# PrintFarmer Project Structure Reorganization Analysis

**Date**: November 27, 2025  
**Analysis Scope**: API Services reorganization for improved separation of concerns

## Executive Summary

The PrintFarmer project has significant opportunity to improve its architecture by moving infrastructure, contracts, and DTOs from `api/Services` to either the `shared` or `infra` projects. This analysis identifies 40+ items that should be reorganized based on:

1. **Cross-project dependencies** (used by tests, other services, or external workers)
2. **Separation of concerns** (infrastructure vs. business logic)
3. **Shared contracts** (interfaces meant for multiple implementations)
4. **External API models** (DTOs from third-party printer APIs)

---

## Current Project Structure

```
src/
├── api/                    # Main ASP.NET Core API server
│   ├── Services/          # ⚠️ MIXED: Business logic + Infrastructure + DTOs
│   ├── Infrastructure/    # ASP.NET-specific infrastructure
│   ├── Controllers/       # REST API endpoints
│   └── ...
├── shared/                # Shared contracts & DTOs
│   ├── Contracts/         # API contracts (Slicing, Auth, etc.)
│   ├── Models.cs          # Domain models
│   └── ISlicerServices.cs # Job queue interface
├── infra/                 # Infrastructure layer (repositories, settings, telemetry)
│   ├── Settings/          # Configuration classes
│   ├── Domain/            # Database entities
│   └── Repositories/      # Data access
└── tests/                 # Test projects
```

---

## Recommended Reorganization

### TIER 1: CRITICAL MOVES (High Priority - 15 items)

These are actively breaking architectural boundaries and causing unnecessary dependencies.

#### **1. External API Models → `shared/Contracts/Printers/`**

**Files to Move:**
- `api/Services/PrusaLinkModels.cs` → `shared/Contracts/Printers/PrusaLinkModels.cs`
- `api/Services/MoonrakerModels.cs` → `shared/Contracts/Printers/MoonrakerModels.cs`

**Reason:**
- These are 100% external API response models, not API business logic
- Used by: Tests, potential external integrations, workers
- No ASP.NET dependencies
- Should be in `shared` for cross-project availability
- Tests currently work around this by importing `Farm.Web.Api.Services`

**Impact:**
- Tests can directly reference printer API models
- Future microservices can reuse these models
- Clearer contract definition

#### **2. SDCP Client Models → `shared/Contracts/Printers/`**

**Files to Move:**
- `api/Services/SdcpClient.cs` (extract model classes, move client interface)

**Models to extract:**
- `SdcpMessage<T>`
- `SdcpData<T>`
- `SdcpStatusResponse`
- `SdcpStatus`
- `SdcpAckResponse`

**Reason:**
- These are protocol-level models, not business logic
- Used by SDCP protocol implementation, should be reusable
- Similar to MoonrakerModels and PrusaLinkModels

---

#### **3. Printer Client Interfaces → `shared/Contracts/Printers/`**

**Files to Move:**
- `api/Services/Interfaces/IMoonrakerClient.cs` → `shared/Contracts/Printers/IMoonrakerClient.cs`
- `api/Services/Interfaces/IPrusaLinkClient.cs` → `shared/Contracts/Printers/IPrusaLinkClient.cs`
- `api/Services/Interfaces/ISdcpClient.cs` → `shared/Contracts/Printers/ISdcpClient.cs`

**Reason:**
- These define contracts for printer communication, not just API details
- Should be shared with: Workers, external integrations, test helpers
- Enable different implementations (mock, real, stub)
- Currently tests work around this by creating test doubles in `Farm.Web.Api.Services`

**Impact:**
- Workers can implement these interfaces directly
- Tests can mock without importing API services
- Clear separation of contract from implementation

---

#### **4. Printer Client Base Class → `infra/Clients/`**

**Files to Move:**
- `api/Services/PrinterClientBase.cs` → `infra/Clients/PrinterClientBase.cs`

**Reason:**
- Shared utilities for implementing printer clients (URL normalization, camera URL handling)
- Needed by: Moonraker, PrusaLink, SDCP implementations
- No ASP.NET Core dependencies
- Infrastructure concern, not API-specific

---

#### **5. Printer Client Implementations → `infra/Clients/`**

**Files to Move:**
- `api/Services/MoonrakerClient.cs` → `infra/Clients/Printers/MoonrakerClient.cs`
- `api/Services/MoonrakerClient.UriOverloads.cs` → `infra/Clients/Printers/MoonrakerClient.UriOverloads.cs`
- `api/Services/PrusaLinkClient.cs` → `infra/Clients/Printers/PrusaLinkClient.cs`
- `api/Services/PrusaLinkApiClient.cs` → `infra/Clients/Printers/PrusaLinkApiClient.cs`
- `api/Services/SdcpClient.cs` → `infra/Clients/Printers/SdcpClient.cs`

**Reason:**
- These are infrastructure implementations of the printer client contracts
- No business logic specific to the API
- Should be usable by workers, other services, microservices
- Belong in `infra` alongside repository patterns

---

#### **6. Virus Scanner Interface & Implementation → `infra/Security/`**

**Files to Move:**
- `api/Services/Interfaces/IVirusScanner.cs` → `infra/Security/IVirusScanner.cs`
- `api/Services/ClamAVVirusScanner.cs` → `infra/Security/ClamAVVirusScanner.cs`

**Reason:**
- Infrastructure security concern, not API-specific
- Should be usable by: File upload handlers, any file processing service
- No ASP.NET dependencies
- Could be used by workers or other services

---

#### **7. Network Utilities → `infra/Network/`**

**Files to Move:**
- `api/Services/NetworkValidationService.cs` → `infra/Network/NetworkValidationService.cs`
- `api/Services/NetworkUrlRewriteService.cs` → `infra/Network/NetworkUrlRewriteService.cs`
- `api/Services/Interfaces/INetworkUrlRewriteService.cs` → `infra/Network/INetworkUrlRewriteService.cs`
- `api/Services/NetworkRangeHelper.cs` → `infra/Network/NetworkRangeHelper.cs`

**Reason:**
- Pure infrastructure utilities for network operations
- No business logic
- Should be usable by: Any service needing network discovery, validation, or URL rewriting
- Used by tests and API equally

---

#### **8. Retry Policy Helper → `infra/Resilience/`**

**Files to Move:**
- `api/Services/RetryPolicyHelper.cs` → `infra/Resilience/RetryPolicyHelper.cs`

**Reason:**
- Generic retry logic utility with no API-specific code
- Should be reusable across all projects (workers, other services)
- Infrastructure cross-cutting concern

---

### TIER 2: IMPORTANT MOVES (Medium Priority - 12 items)

These are more API-specific but should still be considered for shared infrastructure.

#### **9. Printer Capability Discovery → `infra/Services/Printers/`**

**Files to Move:**
- `api/Services/Interfaces/IPrinterCapabilityDiscoveryService.cs` → `infra/Services/Printers/IPrinterCapabilityDiscoveryService.cs`
- `api/Services/PrinterCapabilityDiscoveryService.cs` → `infra/Services/Printers/PrinterCapabilityDiscoveryService.cs`
- `api/Services/PrinterCapabilityUpdateService.cs` → `infra/Services/Printers/PrinterCapabilityUpdateService.cs`

**Reason:**
- Capability discovery is printer infrastructure, not API business logic
- Could be useful for: Workers, external discovery services, tests
- Tests currently test this from API layer directly

---

#### **10. Moonraker Diagnostics → `infra/Diagnostics/Printers/`**

**Files to Move:**
- `api/Services/Interfaces/IMoonrakerDiagnosticsService.cs` → `infra/Diagnostics/IMoonrakerDiagnosticsService.cs`
- `api/Services/MoonrakerDiagnosticsService.cs` → `infra/Diagnostics/MoonrakerDiagnosticsService.cs`

**Reason:**
- Diagnostics utility for Moonraker printers
- Infrastructure utility, not business logic
- Could be used by: Monitoring services, troubleshooting tools, workers

---

#### **11. OCTOPRINT Client → `infra/Clients/Printers/`**

**Files to Move:**
- `api/Services/Interfaces/IOctoPrintClient.cs` → `infra/Clients/Printers/IOctoPrintClient.cs`
- `api/Services/OctoPrintClient.cs` → `infra/Clients/Printers/OctoPrintClient.cs`

**Reason:**
- Another printer API client (pattern matches Moonraker, PrusaLink)
- Should follow the same organizational pattern
- Infrastructure concern

---

#### **12. Spoolman Service → `infra/Clients/Spoolman/`**

**Files to Move:**
- `api/Services/Interfaces/ISpoolmanService.cs` → `infra/Clients/Spoolman/ISpoolmanService.cs`
- `api/Services/SpoolmanService.cs` → `infra/Clients/Spoolman/SpoolmanService.cs`

**Reason:**
- External service integration (Spoolman filament management)
- Infrastructure integration, not business logic
- Could be used by: Multiple API endpoints, future services

---

#### **13. Gcode Services → `shared/Contracts/Gcode/` or `infra/Services/Gcode/`**

**Files to Move:**
- `api/Services/Slicing/IGcodeFilesService.cs` → `shared/Contracts/Gcode/IGcodeFilesService.cs`
- `api/Services/Gcode/GcodeFilesService.cs` → `infra/Services/Gcode/GcodeFilesService.cs`
- `api/Services/Gcode/IGcodeLibraryService.cs` → `shared/Contracts/Gcode/IGcodeLibraryService.cs`
- `api/Services/Gcode/GcodeLibraryService.cs` → `infra/Services/Gcode/GcodeLibraryService.cs`
- `api/Services/Gcode/GcodeMetadataExtractorService.cs` → `infra/Services/Gcode/GcodeMetadataExtractorService.cs`

**Reason:**
- Gcode handling is infrastructure, not API business logic
- Interfaces should be in `shared` so workers can implement them
- Implementations in `infra` for reuse
- Could be used by: Multiple API endpoints, workers, external tools

---

#### **14. Thumbnail Generation → `infra/Services/Thumbnails/`**

**Files to Move:**
- `api/Services/Interfaces/IThumbnailGenerationService.cs` → `infra/Services/Thumbnails/IThumbnailGenerationService.cs`
- `api/Services/ThumbnailGenerationService.cs` → `infra/Services/Thumbnails/ThumbnailGenerationService.cs`

**Reason:**
- Generic image processing utility
- Could be used by: Multiple API endpoints, workers, external services
- No API-specific logic

---

#### **15. Harvest Services → `infra/Services/Gcode/`**

**Files to Move:**
- `api/Services/Interfaces/IGcodeHarvestService.cs` → `infra/Services/Gcode/IGcodeHarvestService.cs`
- `api/Services/GcodeHarvestService.cs` → `infra/Services/Gcode/GcodeHarvestService.cs`
- `api/Services/HarvestCompletionService.cs` → `infra/Services/Gcode/HarvestCompletionService.cs`
- `api/Services/HarvestErrorHelper.cs` → `infra/Services/Gcode/HarvestErrorHelper.cs`
- `api/Services/HarvestWorkerService.cs` → `infra/Services/Gcode/HarvestWorkerService.cs`

**Reason:**
- Gcode harvesting infrastructure
- Could be used by multiple services/workers
- No API-specific logic

---

### TIER 3: CONSIDERATION MOVES (Lower Priority - 13 items)

These are more business-logic oriented but worth reviewing for architectural consistency.

#### **16. Database Initialization → `infra/Database/` (Move interface only)**

**Files to Consider Moving:**
- `api/Services/Interfaces/IDatabaseInitializer.cs` → `infra/Database/IDatabaseInitializer.cs`
- Keep implementation in API (DatabaseInitializer.cs) since it has API-specific initialization

**Reason:**
- Interface defines contract for database initialization
- Could have multiple implementations (API, workers, etc.)
- Shared interface pattern

**Note:** Keep implementation in API as it's specific to ASP.NET Core setup

---

#### **17. Database Seeder → `infra/Database/`**

**Files to Consider Moving:**
- `api/Services/Interfaces/IDatabaseSeeder.cs` → `infra/Database/IDatabaseSeeder.cs`
- `api/Services/DatabaseSeeder.cs` → `infra/Database/DatabaseSeeder.cs`

**Reason:**
- Database seeding is infrastructure concern
- Could be useful for: Deployment scripts, test setup, workers
- No API-specific logic

---

#### **18. Model Analysis Service → `infra/Services/Models/`**

**Files to Consider Moving:**
- `api/Services/Interfaces/IModelAnalysisService.cs` → `infra/Services/Models/IModelAnalysisService.cs`
- `api/Services/ModelAnalysisService.cs` → `infra/Services/Models/ModelAnalysisService.cs`

**Reason:**
- 3D model analysis infrastructure
- Could be used by: Multiple endpoints, workers, external tools
- Pure utility logic

---

#### **19. Thumbnail Harvesting → `infra/Services/Thumbnails/`**

**Files to Consider Moving:**
- `api/Services/Interfaces/IThumbnailGenerationService.cs` (already in Tier 2)
- `api/Services/ThumbnailGenerationService.cs` (already in Tier 2)

---

#### **20. Job Queue & Job Services → `shared/Contracts/Jobs/` + `infra/Services/Jobs/`**

**Files to Consider Moving:**
- `api/Services/Queue/IJobQueueService.cs` → `shared/Contracts/Jobs/IJobQueueService.cs`
- `api/Services/Queue/JobQueueService.cs` → `infra/Services/Jobs/JobQueueService.cs`
- `api/Services/Queue/QueueDataService.cs` → `infra/Services/Jobs/QueueDataService.cs`
- `api/Services/Queue/IQueueService.cs` → `shared/Contracts/Jobs/IQueueService.cs`

**Reason:**
- Queue interfaces are important contracts for distributed systems
- Implementations are infrastructure
- Tests currently need to test queue behavior

---

### TIER 4: SETTINGS & CONFIGURATION (9 items)

#### **21. Settings Classes → Move to `infra/Settings/` (Already Correct Location)**

These are already well-placed but consider if they should be shared:

- `api/Settings/AppSettings.cs` - ✓ Should move to `infra/Settings/AppSettings.cs`
  - Contains application-wide configuration
  - Used by: API, potentially other services
  - Infrastructure concern

---

### TIER 5: AUTHENTICATION & SECURITY (Not Recommended for Moving)

#### **22. Authentication Services - KEEP IN API**

These are **API-specific** and should remain:
- `api/Services/Authentication/AuthenticationService.cs`
- `api/Services/Authentication/TokenRevocationService.cs`
- `api/Services/Authentication/AuthAuditService.cs`
- `api/Services/Authentication/AccountLockoutService.cs`
- `api/Services/Authentication/IAuthenticationService.cs`
- `api/Services/Authentication/ITokenRevocationService.cs`
- `api/Services/Authentication/IAuthAuditService.cs`
- `api/Services/Authentication/IPasswordHashingService.cs`

**Reason:** ASP.NET Core specific, JWT/token based, not needed elsewhere

#### **23. Email Services - KEEP IN API**

These are **API-specific**:
- `api/Services/Email/*`

**Reason:** Email dispatch is API-specific concern, not shared infrastructure

---

### TIER 6: SLICING SERVICES (Complex - Mixed Recommendation)

#### **24. Slicer Profiles - Keep mostly in API, move interfaces to shared**

**Move to `shared/Contracts/Slicing/`:**
- `api/Services/Slicing/IProfilesService.cs`
- `api/Services/Slicing/IOrcaPresetMappingService.cs`

**Keep in API:**
- `api/Services/Slicing/ProfilesService.cs`
- `api/Services/Slicing/ProfileParsingService.cs`
- `api/Services/Slicing/OrcaPresetMappingService.cs`

**Reason:**
- Interfaces define contracts that workers might need
- Implementations are API-specific profile management

---

#### **25. OrcaSlicer Specific - Keep in API**

Keep in `api/Services/Slicing/`:
- `OrcaBundleParsingService.cs`
- `OrcaBundleExportService.cs`
- `ProfileDuplicateFilter.cs`

**Reason:** These are specific to OrcaSlicer profile management, not widely needed

---

## Impact Analysis

### Projects That Would Benefit

1. **Tests** (farm-web.api.tests)
   - Direct access to printer models without importing API services
   - Can mock clients cleanly
   - Better separation from API implementation

2. **Workers** (orcaslicer-worker, prusaslicer-worker)
   - Can reuse printer client interfaces and implementations
   - Can use network utilities for discovery
   - Access to gcode and model services

3. **Infrastructure** (infra project)
   - Becomes the central place for cross-cutting concerns
   - Clear responsibility boundaries
   - Easier to add new implementations

4. **Future Microservices**
   - Can reuse printer clients without importing full API
   - Can implement shared interfaces
   - Clear contract definitions

### Breaking Changes

**Namespace Changes Required:**
- All references to `Farm.Web.Api.Services.PrusaLinkModels` → `Farm.Web.Shared.Contracts.Printers.PrusaLinkModels`
- All references to `Farm.Web.Api.Services.IMoonrakerClient` → `Farm.Infrastructure.Clients.Printers.IMoonrakerClient`
- Similar updates for all moved items

**NuGet References:**
- Tests currently only reference `Farm.Web.Api`
- After moves, will need to reference `Farm.Infrastructure` and `Farm.Web.Shared`
- Project dependencies will be clearer

---

## Implementation Strategy

### Phase 1: Critical Moves (Immediate)
1. Move printer API models (PrusaLinkModels, MoonrakerModels, SdcpClient models)
2. Move printer client interfaces
3. Move printer client implementations
4. Update all references in API, tests

### Phase 2: Infrastructure Consolidation (Next)
1. Move network utilities
2. Move virus scanner
3. Move printer capability services
4. Move retry helpers

### Phase 3: Service Consolidation (Following)
1. Move gcode services
2. Move thumbnail generation
3. Move model analysis
4. Move harvest services

### Phase 4: Settings & Configuration (Last)
1. Consolidate settings to infra
2. Move database initialization interfaces
3. Final reference updates

---

## File Organization Reference

### New `infra/` Structure After Changes

```
infra/
├── Clients/                    # NEW: External service clients
│   ├── Printers/
│   │   ├── IMoonrakerClient.cs
│   │   ├── MoonrakerClient.cs
│   │   ├── IPrusaLinkClient.cs
│   │   ├── PrusaLinkClient.cs
│   │   ├── ISdcpClient.cs
│   │   ├── SdcpClient.cs
│   │   ├── IOctoPrintClient.cs
│   │   ├── OctoPrintClient.cs
│   │   └── PrinterClientBase.cs
│   └── Spoolman/
│       ├── ISpoolmanService.cs
│       └── SpoolmanService.cs
├── Security/                   # NEW: Security utilities
│   ├── IVirusScanner.cs
│   └── ClamAVVirusScanner.cs
├── Network/                    # NEW: Network utilities
│   ├── NetworkValidationService.cs
│   ├── INetworkUrlRewriteService.cs
│   ├── NetworkUrlRewriteService.cs
│   └── NetworkRangeHelper.cs
├── Resilience/                 # NEW: Retry & resilience
│   └── RetryPolicyHelper.cs
├── Services/
│   ├── Printers/              # NEW: Printer services
│   │   ├── IPrinterCapabilityDiscoveryService.cs
│   │   ├── PrinterCapabilityDiscoveryService.cs
│   │   └── PrinterCapabilityUpdateService.cs
│   ├── Gcode/                 # NEW: Gcode services
│   │   ├── IGcodeFilesService.cs
│   │   ├── GcodeFilesService.cs
│   │   ├── IGcodeLibraryService.cs
│   │   ├── GcodeLibraryService.cs
│   │   ├── GcodeMetadataExtractorService.cs
│   │   ├── IGcodeHarvestService.cs
│   │   ├── GcodeHarvestService.cs
│   │   ├── HarvestCompletionService.cs
│   │   ├── HarvestErrorHelper.cs
│   │   └── HarvestWorkerService.cs
│   ├── Models/                # NEW: Model services
│   │   ├── IModelAnalysisService.cs
│   │   └── ModelAnalysisService.cs
│   ├── Thumbnails/            # NEW: Thumbnail services
│   │   ├── IThumbnailGenerationService.cs
│   │   └── ThumbnailGenerationService.cs
│   └── Jobs/                  # NEW: Job queue services
│       ├── IJobQueueService.cs
│       ├── JobQueueService.cs
│       ├── IQueueService.cs
│       └── QueueDataService.cs
├── Diagnostics/               # NEW: Diagnostics services
│   ├── IMoonrakerDiagnosticsService.cs
│   └── MoonrakerDiagnosticsService.cs
└── Database/
    ├── IDatabaseInitializer.cs (moved interface)
    ├── IDatabaseSeeder.cs (moved interface)
    └── (existing structure)
```

### New `shared/` Structure After Changes

```
shared/
├── Contracts/
│   ├── Printers/              # NEW: Printer contracts
│   │   ├── IMoonrakerClient.cs
│   │   ├── IPrusaLinkClient.cs
│   │   ├── ISdcpClient.cs
│   │   ├── PrusaLinkModels.cs
│   │   ├── MoonrakerModels.cs
│   │   └── (SDCP models extracted from SdcpClient.cs)
│   ├── Gcode/                 # NEW: Gcode contracts
│   │   ├── IGcodeFilesService.cs
│   │   └── IGcodeLibraryService.cs
│   ├── Jobs/                  # NEW: Job contracts
│   │   ├── IJobQueueService.cs
│   │   └── IQueueService.cs
│   └── (existing structure)
└── (existing files)
```

### API Services After Changes

```
api/Services/
├── Authentication/            # Remains (API-specific)
├── Email/                      # Remains (API-specific)
├── SignalR/                    # Remains (API-specific)
├── Slicing/                    # Remains (API-specific)
├── SlicerServices/             # Remains (API-specific)
├── Printers/                   # Business logic for printer management
├── Catalog/                    # Business logic for catalog
├── Model/                      # Business logic for model management
├── JobDispatch/                # Business logic for job dispatch
├── Artifacts/                  # Business logic for artifacts
├── SystemLogs/                 # Business logic for system logs
├── Workers/                    # Business logic for worker management
├── FileManagement/             # Business logic for file management
├── Storage/                    # Business logic for storage
├── Users/                      # Business logic for user management
└── (remaining business logic files)
```

---

## Summary Table

| Item | Current Location | Target Location | Priority | Reasoning |
|------|------------------|-----------------|----------|-----------|
| PrusaLinkModels.cs | api/Services | shared/Contracts/Printers | CRITICAL | External API models, used by tests |
| MoonrakerModels.cs | api/Services | shared/Contracts/Printers | CRITICAL | External API models, used by tests |
| SDCP Models | api/Services/SdcpClient.cs | shared/Contracts/Printers | CRITICAL | Protocol models, reusable |
| IMoonrakerClient | api/Services/Interfaces | shared/Contracts/Printers | CRITICAL | Contract for multiple implementations |
| IPrusaLinkClient | api/Services/Interfaces | shared/Contracts/Printers | CRITICAL | Contract for multiple implementations |
| ISdcpClient | api/Services/Interfaces | shared/Contracts/Printers | CRITICAL | Contract for multiple implementations |
| PrinterClientBase | api/Services | infra/Clients | CRITICAL | Shared utilities for clients |
| MoonrakerClient | api/Services | infra/Clients/Printers | CRITICAL | Infrastructure implementation |
| PrusaLinkClient | api/Services | infra/Clients/Printers | CRITICAL | Infrastructure implementation |
| SdcpClient | api/Services | infra/Clients/Printers | CRITICAL | Infrastructure implementation |
| IVirusScanner | api/Services/Interfaces | infra/Security | CRITICAL | Infrastructure utility |
| ClamAVVirusScanner | api/Services | infra/Security | CRITICAL | Infrastructure implementation |
| NetworkValidationService | api/Services | infra/Network | CRITICAL | Network utilities |
| NetworkUrlRewriteService | api/Services | infra/Network | CRITICAL | Network utilities |
| INetworkUrlRewriteService | api/Services/Interfaces | infra/Network | CRITICAL | Network utilities |
| NetworkRangeHelper | api/Services | infra/Network | CRITICAL | Network utilities |
| RetryPolicyHelper | api/Services | infra/Resilience | CRITICAL | Cross-cutting utility |
| IPrinterCapabilityDiscoveryService | api/Services/Interfaces | infra/Services/Printers | IMPORTANT | Infrastructure service |
| PrinterCapabilityDiscoveryService | api/Services | infra/Services/Printers | IMPORTANT | Infrastructure service |
| PrinterCapabilityUpdateService | api/Services | infra/Services/Printers | IMPORTANT | Infrastructure service |
| IMoonrakerDiagnosticsService | api/Services/Interfaces | infra/Diagnostics | IMPORTANT | Infrastructure service |
| MoonrakerDiagnosticsService | api/Services | infra/Diagnostics | IMPORTANT | Infrastructure service |
| IOctoPrintClient | api/Services/Interfaces | infra/Clients/Printers | IMPORTANT | Consistent with other clients |
| OctoPrintClient | api/Services | infra/Clients/Printers | IMPORTANT | Consistent with other clients |
| ISpoolmanService | api/Services/Interfaces | infra/Clients/Spoolman | IMPORTANT | External service client |
| SpoolmanService | api/Services | infra/Clients/Spoolman | IMPORTANT | External service client |
| IGcodeFilesService | api/Services/Slicing | shared/Contracts/Gcode | IMPORTANT | Contract for services |
| GcodeFilesService | api/Services/Gcode | infra/Services/Gcode | IMPORTANT | Infrastructure service |
| IGcodeLibraryService | api/Services/Gcode | shared/Contracts/Gcode | IMPORTANT | Contract for services |
| GcodeLibraryService | api/Services/Gcode | infra/Services/Gcode | IMPORTANT | Infrastructure service |
| GcodeMetadataExtractorService | api/Services/Gcode | infra/Services/Gcode | IMPORTANT | Infrastructure service |
| IThumbnailGenerationService | api/Services/Interfaces | infra/Services/Thumbnails | IMPORTANT | Infrastructure service |
| ThumbnailGenerationService | api/Services | infra/Services/Thumbnails | IMPORTANT | Infrastructure service |
| IGcodeHarvestService | api/Services/Interfaces | infra/Services/Gcode | IMPORTANT | Infrastructure service |
| GcodeHarvestService | api/Services | infra/Services/Gcode | IMPORTANT | Infrastructure service |
| HarvestCompletionService | api/Services | infra/Services/Gcode | IMPORTANT | Infrastructure service |
| HarvestErrorHelper | api/Services | infra/Services/Gcode | IMPORTANT | Infrastructure service |
| HarvestWorkerService | api/Services | infra/Services/Gcode | IMPORTANT | Infrastructure service |
| IDatabaseInitializer | api/Services/Interfaces | infra/Database | CONSIDERATION | Shared contract |
| DatabaseSeeder | api/Services | infra/Database | CONSIDERATION | Infrastructure |
| IModelAnalysisService | api/Services/Interfaces | infra/Services/Models | CONSIDERATION | Infrastructure service |
| ModelAnalysisService | api/Services | infra/Services/Models | CONSIDERATION | Infrastructure service |
| IJobQueueService | api/Services/Queue | shared/Contracts/Jobs | CONSIDERATION | Contract for queue |
| JobQueueService | api/Services/Queue | infra/Services/Jobs | CONSIDERATION | Infrastructure service |
| IQueueService | api/Services/Queue | shared/Contracts/Jobs | CONSIDERATION | Contract for queue |
| QueueDataService | api/Services/Queue | infra/Services/Jobs | CONSIDERATION | Infrastructure service |
| IProfilesService | api/Services/Slicing | shared/Contracts/Slicing | CONSIDERATION | Contract for profiles |
| IOrcaPresetMappingService | api/Services/Slicing | shared/Contracts/Slicing | CONSIDERATION | Contract for mapping |

---

## Next Steps

1. **Review** this analysis with the team
2. **Prioritize** which moves to implement first (recommend TIER 1 first)
3. **Create** new directory structure in `infra/` and `shared/`
4. **Move** files in batches, updating references simultaneously
5. **Update** project file references (`.csproj`)
6. **Run** tests after each batch to verify no breaking changes
7. **Update** documentation with new structure

---

## Questions to Consider

1. Should `api/Services/Models/` business logic stay or move partially to `infra/`?
2. Should there be an `infra/Diagnostics/` namespace or should it be `infra/Services/Diagnostics/`?
3. Should worker classes move to `shared` to allow external worker implementations?
4. Should we create separate NuGet packages for each concern (printers, gcode, slicing)?

