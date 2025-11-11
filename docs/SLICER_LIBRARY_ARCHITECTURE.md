# Slicer Library Architecture Proposal

## Overview

This document proposes reorganizing slicer-related code and assets into separate, independently versioned libraries for each slicer platform. This approach provides better modularity, maintainability, and scalability as new slicer versions and types are added.

## Current State

**Problem**: Slicer code and assets are commingled across the monolithic API and React app:
- **Assets**: `/src/Web/ReactApp/public/assets/orcaslicer/{manufacturer}/{model}_*.{stl,svg,png}`
- **Profiles**: Stored in database (`SlicerProfile` entity), managed by `ProfilesController`
- **Logic**: Scattered across `Services/Slicing/`, worker projects, and frontend services
- **Config**: Version strings hardcoded in submission services (`"PrusaSlicer 2.9.3"`, `"OrcaSlicer 2.3.1"`)
- **Scaling Issue**: Adding a new slicer version requires changes in multiple places

## Proposed Architecture

### 1. Slicer Library Package Structure

Create separate NuGet/npm packages for each slicer version:

```
Slicer Libraries (NuGet packages + npm packages)
├── Farm.Slicers.OrcaSlicer.v2_3_x/
│   ├── lib/ (C# backend)
│   │   ├── Profiles/
│   │   │   ├── official-profiles.json (embedded resource)
│   │   │   └── OrcaSlicerProfilesProvider.cs
│   │   ├── Models/
│   │   │   ├── OrcaSlicerConfig.cs
│   │   │   └── OrcaSlicerMetadata.cs
│   │   ├── Assets/
│   │   │   ├── BedModels/ (*.stl files embedded)
│   │   │   ├── BedTextures/ (*.svg files embedded)
│   │   │   ├── CoverImages/ (*.png files embedded)
│   │   │   └── ManifestRegistry.json
│   │   └── SlicerLibraryExtensions.cs (DI setup)
│   ├── ui/ (React/TypeScript UI)
│   │   ├── components/
│   │   │   ├── OrcaImportWizard.tsx
│   │   │   ├── OrcaBundleExport.tsx
│   │   │   └── OrcaSlicerSettings.tsx
│   │   ├── pages/
│   │   │   └── OrcaProfilesPage.tsx
│   │   ├── services/
│   │   │   ├── orcaProfilesService.ts
│   │   │   └── orcaAssetService.ts
│   │   ├── types/
│   │   │   └── orcaProfiles.ts
│   │   ├── hooks/
│   │   │   └── useOrcaProfiles.ts
│   │   └── index.ts (UI export)
│   ├── assets/ (static assets)
│   │   ├── bed-models/
│   │   ├── bed-textures/
│   │   ├── cover-images/
│   │   └── manifest.json
│   └── package.json / Farm.Slicers.OrcaSlicer.v2_3_x.csproj
│
├── Farm.Slicers.OrcaSlicer.v2_4_x/ (future - same structure)
├── Farm.Slicers.PrusaSlicer.v2_9_x/
│   ├── lib/ (C# backend - same as OrcaSlicer)
│   ├── ui/ (React/TypeScript UI)
│   │   ├── components/
│   │   ├── pages/
│   │   ├── services/
│   │   ├── types/
│   │   ├── hooks/
│   │   └── index.ts (UI export)
│   ├── assets/
│   └── package.json / Farm.Slicers.PrusaSlicer.v2_9_x.csproj
│
└── Farm.Slicers.PrusaSlicer.v3_0_x/ (future - same structure)
```

### 2. Backend Library Components

Each slicer library would contain:

#### `ISlicerLibrary` (Base Interface)
```csharp
public interface ISlicerLibrary
{
    string SlicerName { get; }           // "OrcaSlicer"
    string SlicerVersion { get; }        // "2.3.1"
    string SlicerType { get; }           // "OrcaSlicer" (enum-like)
    
    // Profile management
    ISlicerProfilesProvider ProfilesProvider { get; }
    
    // Asset management
    ISlicerAssetRegistry AssetRegistry { get; }
    
    // Configuration validation
    Task<SlicerConfigValidationResult> ValidateConfigAsync(SlicerConfig config, CancellationToken ct);
}
```

#### `ISlicerProfilesProvider`
```csharp
public interface ISlicerProfilesProvider
{
    Task<IEnumerable<SlicerProfileMetadata>> ListOfficialProfilesAsync(CancellationToken ct);
    Task<SlicerProfileJson?> GetProfileJsonAsync(string profileId, CancellationToken ct);
    string GetProfilesVersion();
}
```

#### `ISlicerAssetRegistry`
```csharp
public interface ISlicerAssetRegistry
{
    // Get asset by printer model
    Task<SlicerAsset?> GetAssetAsync(string manufacturerName, string modelName, CancellationToken ct);
    
    // List all available assets
    Task<IEnumerable<SlicerAsset>> ListAssetsAsync(CancellationToken ct);
    
    // Get embedded asset stream (bed model, texture, cover image)
    Stream? GetBedModelStream(string manufacturerName, string modelName);
    Stream? GetBedTextureStream(string manufacturerName, string modelName);
    Stream? GetCoverImageStream(string manufacturerName, string modelName);
}
```

#### `SlicerAsset` Model
```csharp
public record SlicerAsset(
    string ManufacturerName,
    string ModelName,
    bool HasBedModel,
    bool HasBedTexture,
    string? BedTextureFormat,  // "svg" or "png"
    bool HasCoverImage,
    string SlicerLibraryVersion
);
```

#### `ISlicerUIProvider` (NEW - for slicer-specific UI)
```csharp
public interface ISlicerUIProvider
{
    string SlicerName { get; }           // "OrcaSlicer"
    string SlicerVersion { get; }        // "2.3.1"
    
    // UI component availability
    bool HasBundleImport { get; }        // OrcaSlicer has bundle import
    bool HasAssetCustomization { get; }  // Specific bed texture/model options
    
    // Configuration schema for this slicer version
    Type ProfileConfigType { get; }      // OrcaSlicerProfile vs PrusaSlicerProfile
    Type SettingsType { get; }           // Slicer-specific settings model
}
```

### 3. Frontend/React Library Components

Each slicer library exports UI components from `/ui` directory:

#### `ISlicerUIRegistry` (NEW - Frontend Service)
```typescript
export interface ISlicerUIRegistry {
  // Register slicer-specific UI
  registerUI(slicerName: string, slicerVersion: string, ui: SlicerUIExports): void;
  
  // Get UI components for specific slicer
  getImportComponent(slicerName: string): React.ComponentType<ImportComponentProps> | null;
  getSettingsComponent(slicerName: string): React.ComponentType<SettingsComponentProps> | null;
  getProfileEditorComponent(slicerName: string): React.ComponentType<EditorProps> | null;
  
  // Get asset manifest
  getAssetManifest(slicerName: string): SlicerAssetManifest | null;
}
```

#### Example: OrcaSlicer UI Export (`ui/index.ts`)
```typescript
// @farm/slicers-orcaslicer-v2_3_x/ui

export const OrcaSlicerUI = {
  slicerName: "OrcaSlicer",
  slicerVersion: "2.3.1",
  
  // Components
  ImportComponent: OrcaImportWizard,  // Bundle import workflow
  SettingsComponent: OrcaSlicerSettings,  // Engine-specific settings
  ProfileEditorComponent: OrcaProfileEditor,  // Profile customization UI
  
  // Services
  profilesService: orcaProfilesService,  // Bundle import/export
  assetService: orcaAssetService,  // Bed textures, printer covers
  
  // Types
  types: {
    ProfileConfig: OrcaSlicerProfile,
    BundlePreview: OrcaBundlePreview,
    PresetMatch: OrcaPresetMatch,
  },
  
  // Asset manifest
  assetManifest: require("./assets/manifest.json"),
  
  // Custom hooks
  useOrcaProfiles: useOrcaProfiles,
  useOrcaBundles: useOrcaBundles,
};
```

#### OrcaSlicer UI Components (`ui/components/`)
- `OrcaImportWizard.tsx` — Multi-step bundle import (moved from core)
- `OrcaBundleExport.tsx` — Export profiles as OrcaSlicer bundle
- `OrcaSlicerSettings.tsx` — OrcaSlicer-specific worker configuration
- `OrcaProfileEditor.tsx` — Edit OrcaSlicer profile properties

#### OrcaSlicer UI Services (`ui/services/`)
- `orcaProfilesService.ts` — Bundle import/export operations (moved from core)
- `orcaAssetService.ts` — Bed texture/cover image management (moved from core)

#### OrcaSlicer UI Types (`ui/types/`)
- `orcaProfiles.ts` — OrcaSlicer-specific profile types
- `orcaBundles.ts` — Bundle import/export types

### 4. Frontend Library Components (Shared)

Each slicer npm package would contain:

```typescript
Each slicer library exports backend and frontend components for unified versioning.

**Backend Export** (`Farm.Slicers.OrcaSlicer.v2_3_x.csproj`):
```csharp
public class OrcaSlicerLibraryExtensions
{
    public static IServiceCollection AddOrcaSlicerLibrary(this IServiceCollection services)
    {
        services.AddSingleton<ISlicerLibrary>(new OrcaSlicerLibrary_v2_3_x());
        services.AddSingleton<ISlicerUIProvider>(new OrcaSlicerUIProvider_v2_3_x());
        return services;
    }
}
```

**Frontend Export** (`@farm/slicers-orcaslicer-v2_3_x/ui`):
```typescript
export * from "./components";
export * from "./services";
export * from "./types";
export * from "./hooks";
export { OrcaSlicerUI } from "./index";
```

### 5. Integration Points
```

### 6. Integration Architecture

#### Backend Startup (Program.cs)
```csharp
services
  // Register slicer libraries (includes both backend code + UI metadata)
  .AddOrcaSlicerLibrary()      // v2_3_x
  .AddPrusaSlicerLibrary()     // v2_9_x
  // Registry aggregates all registered libraries
  .AddSlicerRegistry()         // ISlicerRegistry service
  .AddSlicingServices();
```

#### Frontend Startup (React App, `App.tsx`)
```typescript
import { SlicerUIRegistry } from "@farm/core/slicers";
import { OrcaSlicerUI } from "@farm/slicers-orcaslicer-v2_3_x/ui";
import { PrusaSlicerUI } from "@farm/slicers-prusaslicer-v2_9_x/ui";

const slicerUIRegistry = new SlicerUIRegistry();
slicerUIRegistry.registerUI("OrcaSlicer", "2.3.1", OrcaSlicerUI);
slicerUIRegistry.registerUI("PrusaSlicer", "2.9.3", PrusaSlicerUI);

// Use in pages
<SlicerUIProvider registry={slicerUIRegistry}>
  {/* Pages automatically load slicer-specific UI */}
</SlicerUIProvider>
```

#### Backend Service: `ISlicerRegistry` (NEW)
```csharp
public interface ISlicerRegistry
{
    // Get specific library
    ISlicerLibrary? GetLibrary(string slicerName, string version);
    
    // Get all libraries for a slicer type
    IEnumerable<ISlicerLibrary> GetLibraries(string slicerName);
    
    // Get UI metadata for a library
    ISlicerUIProvider? GetUIProvider(string slicerName, string version);
    
    // Get latest version of a slicer
    ISlicerLibrary? GetLatestLibrary(string slicerName);
    
    // List all available libraries
    IEnumerable<ISlicerLibrary> ListAllLibraries();
}
```

#### Frontend Service: `ISlicerUIRegistry` (NEW)
```typescript
export interface ISlicerUIRegistry {
  // Register slicer UI exports
  registerUI(slicerName: string, slicerVersion: string, ui: SlicerUIExports): void;
  
  // Get UI components for a slicer
  getImportComponent(slicerName: string): React.ComponentType<any> | null;
  getSettingsComponent(slicerName: string): React.ComponentType<any> | null;
  getProfileEditorComponent(slicerName: string): React.ComponentType<any> | null;
  
  // Get services specific to a slicer
  getProfilesService(slicerName: string): SlicerProfilesService | null;
  getAssetService(slicerName: string): SlicerAssetService | null;
  
  // Get types/schemas for a slicer
  getProfileConfig(slicerName: string): Type | null;
  
  // List all registered slicers
  listRegisteredSlicers(): Array<{ name: string; version: string }>;
}
```

#### Service: `ISlicerAssetService` (UPDATED)
```csharp
public interface ISlicerAssetService
{
    Task<SlicerAsset?> GetAssetAsync(
        string slicerName, 
        string slicerVersion, 
        string manufacturerName, 
        string modelName, 
        CancellationToken ct);
    
    // Stream-based asset delivery (no file I/O needed)
    Stream? GetBedModelStream(string slicerName, string version, string manufacturer, string model);
    Stream? GetBedTextureStream(string slicerName, string version, string manufacturer, string model);
    Stream? GetCoverImageStream(string slicerName, string version, string manufacturer, string model);
}
```

### 7. Migration Path

#### Phase 1: Create Library Abstractions
- Define backend interfaces: `ISlicerLibrary`, `ISlicerProfilesProvider`, `ISlicerAssetRegistry`, `ISlicerUIProvider`
- Define frontend interfaces: `ISlicerUIRegistry`, `SlicerUIExports`
- Create `ISlicerRegistry` backend service
- Create `SlicerUIRegistry` frontend service

#### Phase 2: Extract OrcaSlicer v2.3.1 Library
- Create `Farm.Slicers.OrcaSlicer.v2_3_x` NuGet + npm package
- **Backend**: Embed `/public/assets/orcaslicer/` as resources; implement `ISlicerLibrary` + `ISlicerUIProvider`
- **Frontend**: Move `OrcaImportWizard`, `orcaProfilesService`, `orcaAssetService`, related types to `/ui`
- Export UI components and services via `ui/index.ts`
- Update core API to use library instead of file-based assets
- Update core React app to import `OrcaSlicerUI` and register with `SlicerUIRegistry`

#### Phase 3: Extract PrusaSlicer v2.9.3 Library
- Create `Farm.Slicers.PrusaSlicer.v2_9_x` NuGet + npm package
- **Backend**: Implement `ISlicerLibrary` + `ISlicerUIProvider` (same as OrcaSlicer)
- **Frontend**: Create `/ui` with PrusaSlicer-specific components, services, types
- Register UI with `SlicerUIRegistry`

#### Phase 4: Refactor Core Services to Use Registries
- **Backend**: Refactor `ISlicerAssetService` + `ProfilesController` to use `ISlicerRegistry`
- Remove hardcoded version strings from core code
- **Frontend**: Refactor core pages to use `SlicerUIRegistry` for dynamic component loading
- Replace inline UI logic with dynamically loaded slicer-specific components
- Update `NewSliceJobPage`, `SlicerProfilesPage` to be slicer-agnostic

#### Phase 5: Testing & Documentation
- Write tests for `ISlicerUIRegistry` integration
- Update documentation for adding new slicer versions
- Verify backward compatibility with existing profiles
- Performance testing: ensure dynamic UI loading doesn't degrade performance

### 8. Benefits

| Benefit | Impact |
|---------|--------|
| **Modularity** | Each slicer version is independently versioned and deployable with UI included |
| **Scalability** | Adding new slicer versions requires only adding new packages, not refactoring core code |
| **Maintainability** | Slicer-specific UI stays synchronized with backend code; easier to understand and modify |
| **Version Agility** | Property renames/additions in new slicer versions can be handled within library UI without core changes |
| **Reusability** | Libraries (backend + UI) can be used by other projects (CLI, desktop app, mobile, etc.) |
| **Performance** | Embedded resources avoid file I/O; dynamic UI loading only loads active slicer components |
| **CI/CD** | Each library can have its own release cycle, testing, and versioning |
| **Documentation** | Each library is self-documenting with its own README, schemas, and UI specs |
| **Testing** | Easier to unit test slicer-specific functionality (backend + UI) in isolation |
| **UI Flexibility** | Different slicer versions can have completely different UI/UX without core page changes |

### 9. Example: Adding OrcaSlicer v2.9.4

Once the architecture is in place, adding a new version requires only:

1. Create `Farm.Slicers.OrcaSlicer.v2_9_x` NuGet + npm package
2. **Backend**: Implement `ISlicerLibrary` + `ISlicerUIProvider`
3. **Frontend**: Create `/ui` with components, services, types if profile schema changed
4. Register backend in Program.cs: `.AddOrcaSlicerLibrary()`
5. Register frontend in App.tsx: `slicerUIRegistry.registerUI("OrcaSlicer", "2.9.4", OrcaSlicerUI_v2_9_4)`
6. **No changes to core API controllers, core pages, or services needed**

### 10. Deployment Considerations

- **NuGet Package**: Distributed via internal or public NuGet feed (backend + UI metadata)
- **npm Package**: Distributed via npm registry or monorepo (backend type definitions + React UI)
- **Monorepo Strategy**: Recommended — keep `@farm/slicers-orcaslicer-v2_3_x` with both `/lib` (TS types) and `/ui` (React components)
- **Versioning**: Use `major.minor.patch` matching slicer version (e.g., v2.3.1 → 2.3.1)
- **Documentation**: Each library includes its own README with property schemas, UI specs, asset specifications
- **Asset Hosting**: Embed assets in npm package; serve statically from React app build or CDN
- **Bundle Size**: Consider lazy-loading slicer UI only when needed; use code splitting for each slicer library

### 11. Example Implementation Roadmap

```
Q1 2025
├── Week 1-2: Design ISlicerLibrary abstractions
├── Week 3-4: Implement SlicerRegistry
├── Week 5-8: Create Farm.Slicers.OrcaSlicer.v2_3_x
└── Week 9-12: Refactor API services to use registry

Q2 2025
├── Week 1-4: Create Farm.Slicers.PrusaSlicer.v2_9_x
├── Week 5-8: Update frontend asset service
└── Week 9-12: Testing, documentation, production release
```

## Files to Create/Migrate

### Backend (C# / NuGet)
- `src/api/Program.cs` - Register slicer libraries via `.AddOrcaSlicerLibrary()` etc.
- `src/api/Interfaces/ISlicerUIProvider.cs` - New interface for UI metadata
- `src/api/Interfaces/ISlicerRegistry.cs` - Update to include UI provider lookup
- `src/api/Controllers/Slicing/ProfilesController.cs` - Refactor to use `ISlicerRegistry`
- `src/api/Services/Slicing/AssetService.cs` - Refactor to use `ISlicerAssetRegistry`
- `src/Slicers/Farm.Slicers.OrcaSlicer.v2_3_x/` - New library package
- `src/Slicers/Farm.Slicers.PrusaSlicer.v2_9_x/` - New library package

### Frontend (TypeScript / npm)
- `src/Web/ReactApp/src/App.tsx` - Initialize `SlicerUIRegistry` and register slicer UIs
- `src/Web/ReactApp/src/contexts/SlicerUIContext.ts` - New context for UI registry
- `src/Web/ReactApp/src/pages/NewSliceJobPage.tsx` - Refactor to use `ISlicerUIRegistry` for dynamic components
- `src/Web/ReactApp/src/pages/SlicerProfilesPage.tsx` - Refactor to load slicer-specific import UI
- `@farm/slicers-orcaslicer-v2_3_x/ui/` - New directory for OrcaSlicer UI (move `OrcaImportWizard`, etc.)
- `@farm/slicers-orcaslicer-v2_3_x/ui/index.ts` - Export all UI components and services
- `@farm/slicers-prusaslicer-v2_9_x/ui/` - New directory for PrusaSlicer UI
- `@farm/slicers-prusaslicer-v2_9_x/ui/index.ts` - Export all UI components and services

### Database
- Add `SlicerUIProviderVersion` tracking if UI schema versioning needed
- Update profile entities to reference slicer library version

## Questions for Team Review

**Architecture**
1. Should each slicer library be a separate git repository or part of monorepo?
   - Recommendation: Monorepo with `/src/Slicers/{LibraryName}` for easier dependency management

2. Should backend library (NuGet) and frontend library (npm) be separate packages?
   - Recommendation: Keep in same monorepo; export both NuGet (backend) and npm (frontend UI) from same source

**Versioning & Maintenance**
3. What's the versioning strategy - match slicer version exactly or use separate versioning?
   - Recommendation: Match slicer version exactly (e.g., OrcaSlicer 2.3.1 → package v2.3.1)

4. How to handle legacy profiles/assets when deprecating a slicer version?
   - Option A: Keep all historical versions; mark deprecated versions as warnings in UI
   - Option B: Maintain only current + previous major version
   - Recommendation: Option A for maximum compatibility; add deprecation warnings

**UI & Components**
5. Should slicer UI be lazy-loaded or bundled at build time?
   - Recommendation: Lazy-load using dynamic imports; improves bundle size for users not using all slicers

6. How to handle property schema differences between slicer versions?
   - Recommendation: Each library exports its own profile config type; registry handles type resolution

7. Should core pages (e.g., `NewSliceJobPage`) be completely slicer-agnostic?
   - Recommendation: Yes — core pages should only know about `ISlicerUIRegistry` interface, not specific slicers

**Assets & Resources**
8. Should assets (bed textures, cover images, bed models) be embedded or external?
   - Recommendation: Embed in npm package for deployment simplicity; serve from React app build

9. How to ensure assets are updated when slicer library is updated?
   - Recommendation: Assets versioned with library; embed in npm package; no separate asset management needed

---

**Document Version**: 1.0  
**Last Updated**: 2025-11-11  
**Author**: Architecture Review
