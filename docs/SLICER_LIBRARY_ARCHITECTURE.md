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
├── Farm.Slicers.OrcaSlicer.v2_3_1/
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
│   └── package.json / Farm.Slicers.OrcaSlicer.v2_3_1.csproj
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

**Backend Export** (`Farm.Slicers.OrcaSlicer.v2_3_1.csproj`):
```csharp
public class OrcaSlicerLibraryExtensions
{
    public static IServiceCollection AddOrcaSlicerLibrary(this IServiceCollection services)
    {
        services.AddSingleton<ISlicerLibrary>(new OrcaSlicerLibrary_v2_3_1());
        services.AddSingleton<ISlicerUIProvider>(new OrcaSlicerUIProvider_v2_3_1());
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

### 5. Plugin Discovery System (Implemented)

Rather than hardcoding slicer library registration, PrintFarmer uses a **plugin discovery system** based on assembly attributes. This allows slicer libraries to be auto-discovered and registered without any changes to the core API code.

#### `SlicerPluginAttribute` (Assembly-Level Attribute)

Each slicer library declares itself as a plugin using an assembly-level attribute:

```csharp
namespace Farm.Web.Shared.Contracts.Slicing.Libraries;

/// <summary>
/// Marks an assembly as containing a slicer library plugin.
/// Enables automatic discovery and registration via reflection.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public class SlicerPluginAttribute : Attribute
{
    public Type LibraryType { get; }      // Must implement ISlicerLibrary
    public Type UIProviderType { get; }   // Must implement ISlicerUIProvider
    
    public SlicerPluginAttribute(Type libraryType, Type uiProviderType) { ... }
}
```

#### Usage in Slicer Library

In `AssemblyInfo.cs` of each slicer library project:

```csharp
// Farm.Slicers.OrcaSlicer.v2_3_1/AssemblyInfo.cs
using System.Reflection;
using Farm.Web.Shared.Contracts.Slicing.Libraries;
using Farm.Slicers.OrcaSlicer.v2_3_1;

[assembly: SlicerPlugin(
    typeof(OrcaSlicerLibrary_v2_3_1),
    typeof(OrcaSlicerUIProvider_v2_3_1)
)]
```

#### `SlicerPluginDiscovery` Service

The plugin discovery service scans all loaded assemblies at startup:

```csharp
namespace Farm.Web.Api.Services.Slicing.Abstractions;

/// <summary>
/// Discovers and loads slicer library plugins from referenced assemblies.
/// Uses SlicerPluginAttribute to find and register implementations.
/// </summary>
public static class SlicerPluginDiscovery
{
    /// <summary>
    /// Scans assemblies for SlicerPluginAttribute and loads all plugins.
    /// </summary>
    public static IServiceCollection DiscoverAndRegisterSlicerPlugins(
        this IServiceCollection services)
    {
        // 1. Get all assemblies in current domain
        // 2. Scan each assembly for SlicerPluginAttribute
        // 3. Verify types implement required interfaces
        // 4. Instantiate and collect library/UI provider pairs
        // 5. Return for AddSlicerRegistry() to complete registration
    }
    
    /// <summary>
    /// Creates the slicer registry with discovered plugins.
    /// </summary>
    public static IServiceCollection AddSlicerRegistry(
        this IServiceCollection services)
    {
        var registry = new SlicerRegistry(libraries, uiProviders);
        return services.AddSingleton<ISlicerRegistry>(registry);
    }
}
```

#### How It Works

1. **Build Time**: When OrcaSlicer library is referenced in API project, it becomes part of the loaded assemblies
2. **Startup**: Application calls `services.DiscoverAndRegisterSlicerPlugins()`
3. **Discovery**: Plugin discovery reflects over all loaded assemblies looking for `SlicerPluginAttribute`
4. **Instantiation**: For each plugin found, creates instances of the library and UI provider types
5. **Registration**: Collects instances and passes to `AddSlicerRegistry()` to create unified registry

#### Benefits of Plugin Discovery

| Benefit | Details |
|---------|---------|
| **Zero-Config** | New slicer versions auto-discovered; no code changes needed in core API |
| **Loose Coupling** | Core API has no dependencies on specific slicer implementations |
| **Scale Easily** | Add new slicer version → add project reference + `AssemblyInfo.cs` attribute |
| **Future-Proof** | Multiple plugins can be loaded from different assemblies simultaneously |
| **Type Safe** | Attribute validation ensures all plugins implement required interfaces |
| **Error Handling** | Clear error messages if plugin types don't implement interfaces |

### 6. Integration Architecture


#### Backend Startup (Program.cs) - Plugin Approach

With the plugin discovery system, startup is automatic:

```csharp
// src/api/Infrastructure/ServiceCollectionExtensions.cs
public static IServiceCollection AddPrintFarmerServices(
    this IServiceCollection services, 
    IConfiguration configuration)
{
    // ... other services ...
    
    // Slicer Library Registration
    // Dynamically discover and register all slicer library plugins
    // using SlicerPluginAttribute. Each slicer library (OrcaSlicer, 
    // PrusaSlicer, etc.) declares itself via assembly attribute.
    _ = services
        .DiscoverAndRegisterSlicerPlugins()    // Auto-discover all plugins
        .AddSlicerRegistry();                  // Create unified registry
    
    // ... other services ...
    return services;
}
```

**What happens automatically:**
1. OrcaSlicer library project is referenced → added to loaded assemblies
2. `DiscoverAndRegisterSlicerPlugins()` is called at startup
3. Plugin discovery finds `SlicerPluginAttribute` in OrcaSlicer assembly
4. Instantiates `OrcaSlicerLibrary_v2_3_1` and `OrcaSlicerUIProvider_v2_3_1`
5. `AddSlicerRegistry()` creates `ISlicerRegistry` service with all discovered plugins
6. API endpoints use `ISlicerRegistry` to access profiles, assets, UI metadata

**To add PrusaSlicer:**
1. Add project reference: `<ProjectReference Include="..\Slicers\Farm.Slicers.PrusaSlicer.v2_9_x\..." />`
2. Add to OrcaSlicer: Done! The discovery process automatically picks it up.

No changes needed to `ServiceCollectionExtensions.cs`.

#### Frontend Startup (React App, `App.tsx`) - Manual Registration

The React app still requires explicit registration (due to how npm/module loading works):

```typescript
// src/Web/ReactApp/src/App.tsx
import { QueryClientProvider } from '@tanstack/react-query';
import { AuthProvider } from '@/contexts/AuthContext';
import { ThemeProvider } from '@/contexts/ThemeContext';
import { SlicerUIProvider } from '@/contexts/SlicerUIContext';

// Import slicer UI exports from their npm packages
// (In future, these could also be dynamically loaded)
import { OrcaSlicerUI } from '@farm/slicers-orcaslicer-v2_3_x/ui';
import { PrusaSlicerUI } from '@farm/slicers-prusaslicer-v2_9_x/ui';

function App() {
  return (
    <ErrorBoundary>
      <ThemeProvider>
        <AuthProvider>
          <QueryClientProvider client={queryClient}>
            {/* SlicerUIProvider wraps app with slicer UI registry */}
            <SlicerUIProvider>
              {/* Inside App, components can use useSlicerUIRegistry() */}
              <Router>
                <AuthenticatedAppRoutes />
              </Router>
            </SlicerUIProvider>
            <Toaster position="top-right" richColors />
          </QueryClientProvider>
        </AuthProvider>
      </ThemeProvider>
    </ErrorBoundary>
  );
}

export default App;
```

Inside the `SlicerUIProvider`, components can access the registry:

```typescript
import { useSlicerUIRegistry } from '@/contexts/useSlicerUIRegistry';

function NewSliceJobPage() {
  const slicerUIRegistry = useSlicerUIRegistry();
  
  // Get UI components for a specific slicer
  const ImportComponent = slicerUIRegistry.getComponent('OrcaSlicer', 'import');
  const SettingsComponent = slicerUIRegistry.getComponent('OrcaSlicer', 'settings');
  
  return (
    <div>
      {ImportComponent && <ImportComponent {...props} />}
      {SettingsComponent && <SettingsComponent {...props} />}
    </div>
  );
}
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

### 9. Migration Path

#### Phase 1: Create Library Abstractions ✅ Completed
- ✅ Define backend interfaces: `ISlicerLibrary`, `ISlicerProfilesProvider`, `ISlicerAssetRegistry`, `ISlicerUIProvider`
- ✅ Define frontend interfaces: `ISlicerUIRegistry`, `SlicerUIExports`
- ✅ Create `ISlicerRegistry` backend service and implementation
- ✅ Create `SlicerUIRegistry` frontend service
- ✅ Create `SlicerPluginAttribute` for assembly-level plugin declaration
- ✅ Create `SlicerPluginDiscovery` for automatic plugin loading

#### Phase 2: Extract OrcaSlicer v2.3.1 Library ✅ Completed (Core)
- ✅ Create `Farm.Slicers.OrcaSlicer.v2_3_1` NuGet project structure
- ✅ Create `Farm.Slicers.OrcaSlicer.v2_3_1` npm package placeholder
- ✅ Implement `ISlicerLibrary` in `OrcaSlicerLibrary_v2_3_1`
- ✅ Implement `ISlicerUIProvider` in `OrcaSlicerUIProvider_v2_3_1`
- ✅ Implement `ISlicerProfilesProvider` in `OrcaSlicerProfilesProvider`
- ✅ Implement `ISlicerAssetRegistry` in `OrcaSlicerAssetRegistry`
- ✅ Create `AssemblyInfo.cs` with `SlicerPluginAttribute`
- ✅ Create `SlicerUIContext`, `SlicerUIProvider`, `useSlicerUIRegistry` hook
- ✅ Create OrcaSlicer UI export file (`ui/index.ts`)
- ✅ Integrate plugin discovery in `ServiceCollectionExtensions.cs`
- ⏳ Migrate OrcaSlicer UI components (`OrcaImportWizard`, services, types)
- ⏳ Integrate OrcaSlicer UI exports into React app
- ⏳ Create embedded resource profiles and assets

#### Phase 3: Extract PrusaSlicer v2.9.3 Library
- ⏳ Create `Farm.Slicers.PrusaSlicer.v2_9_x` NuGet + npm package
- ⏳ Implement `ISlicerLibrary` + `ISlicerUIProvider` (same as OrcaSlicer)
- ⏳ Create `AssemblyInfo.cs` with `SlicerPluginAttribute`
- ⏳ Frontend: Create `/ui` with PrusaSlicer-specific components, services, types
- ⏳ Update React app to import and register `PrusaSlicerUI`

#### Phase 4: Refactor Core Services to Use Registries
- ⏳ **Backend**: Refactor `ISlicerAssetService` + `ProfilesController` to use `ISlicerRegistry`
- ⏳ Remove hardcoded version strings from core code
- ⏳ **Frontend**: Refactor core pages to use `SlicerUIRegistry` for dynamic component loading
- ⏳ Replace inline UI logic with dynamically loaded slicer-specific components
- ⏳ Update `NewSliceJobPage`, `SlicerProfilesPage` to be slicer-agnostic

#### Phase 5: Testing & Documentation
- ⏳ Write tests for `SlicerPluginDiscovery` with multiple plugins
- ⏳ Write tests for `ISlicerUIRegistry` integration
- ⏳ Update documentation for adding new slicer versions
- ⏳ Verify backward compatibility with existing profiles
- ⏳ Performance testing: ensure dynamic UI loading doesn't degrade performance

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

### 10. Quick Start: Adding a New Slicer Library

#### Backend: Adding OrcaSlicer v2.9.4 (New Version)

With the plugin discovery system, adding a new slicer version is straightforward:

**Step 1: Create Project**
```bash
cd /src/Slicers
mkdir Farm.Slicers.OrcaSlicer.v2_9_x
cd Farm.Slicers.OrcaSlicer.v2_9_x
```

**Step 2: Create `.csproj`**
```xml
<!-- Farm.Slicers.OrcaSlicer.v2_9_x/Farm.Slicers.OrcaSlicer.v2_9_x.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <Version>2.9.4</Version>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../shared/Farm.Web.Shared.csproj" />
  </ItemGroup>
  <ItemGroup>
    <EmbeddedResource Include="lib/Profiles/official-profiles.json" />
    <EmbeddedResource Include="lib/Assets/**/*" />
  </ItemGroup>
</Project>
```

**Step 3: Implement ISlicerLibrary**
```csharp
// Farm.Slicers.OrcaSlicer.v2_9_x/lib/Core/OrcaSlicerLibrary_v2_9_x.cs
using Farm.Web.Shared.Contracts.Slicing.Libraries;

namespace Farm.Slicers.OrcaSlicer.v2_9_x;

public class OrcaSlicerLibrary_v2_9_x : ISlicerLibrary
{
    public string SlicerName => "OrcaSlicer";
    public string SlicerVersion => "2.9.4";
    public string SlicerType => "OrcaSlicer";
    
    public ISlicerProfilesProvider ProfilesProvider { get; }
    public ISlicerAssetRegistry AssetRegistry { get; }
    
    public OrcaSlicerLibrary_v2_9_x()
    {
        ProfilesProvider = new OrcaSlicerProfilesProvider();
        AssetRegistry = new OrcaSlicerAssetRegistry();
    }
}
```

**Step 4: Declare Plugin**
```csharp
// Farm.Slicers.OrcaSlicer.v2_9_x/AssemblyInfo.cs
using System.Reflection;
using Farm.Web.Shared.Contracts.Slicing.Libraries;
using Farm.Slicers.OrcaSlicer.v2_9_x;

[assembly: SlicerPlugin(
    typeof(OrcaSlicerLibrary_v2_9_x),
    typeof(OrcaSlicerUIProvider_v2_9_x)
)]
```

**Step 5: Add Project Reference to API**
```xml
<!-- src/api/Farm.Web.Api.csproj -->
<ItemGroup>
  <ProjectReference Include="..\Slicers\Farm.Slicers.OrcaSlicer.v2_3_1\..." />
  <ProjectReference Include="..\Slicers\Farm.Slicers.OrcaSlicer.v2_9_x\..." />  <!-- NEW -->
</ItemGroup>
```

**That's it!** The plugin discovery system automatically picks up the new version on next startup.

#### Frontend: Adding PrusaSlicer UI

**Step 1: Create UI Directory**
```
src/Slicers/Farm.Slicers.PrusaSlicer.v2_9_x/
└── ui/
    ├── components/
    │   ├── PrusaImportWizard.tsx
    │   └── PrusaSettings.tsx
    ├── services/
    │   └── prusaProfilesService.ts
    ├── types/
    │   └── prusaProfiles.ts
    └── index.ts
```

**Step 2: Export UI**
```typescript
// src/Slicers/Farm.Slicers.PrusaSlicer.v2_9_x/ui/index.ts
export { PrusaImportWizard } from './components/PrusaImportWizard';
export { PrusaSettings } from './components/PrusaSettings';
export { prusaProfilesService } from './services/prusaProfilesService';
export type { PrusaProfile } from './types/prusaProfiles';
```

**Step 3: Register in React App**
```typescript
// src/Web/ReactApp/src/App.tsx
import { PrusaSlicerUI } from '@farm/slicers-prusaslicer-v2_9_x/ui';

function App() {
  return (
    <SlicerUIProvider>
      {/* OrcaSlicer already auto-registered; PrusaSlicer UI now available */}
    </SlicerUIProvider>
  );
}
```

### 11. Benefits of Plugin Discovery System

| Benefit | Details |
|---------|---------|
| **Zero Config** | New slicer versions auto-discovered via reflection; no hardcoded registrations |
| **Scalable** | Supporting 5 slicer versions needs same ServiceCollectionExtensions code as 1 version |
| **Loose Coupling** | Core API has zero dependencies on specific slicer implementations |
| **Future Proof** | Plugins can be loaded from different assemblies, NuGet packages, or dynamically at runtime |
| **Type Safe** | Assembly attribute validates all plugins implement required interfaces |
| **Error Handling** | Clear error messages if plugin types don't implement interfaces |
| **Easy Debugging** | Debug output shows which plugins were discovered and loaded |

### 8. Benefits of Slicer Library Architecture

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

### 9. Example: Deployment with Multiple Slicer Versions

**Current State (After OrcaSlicer v2.3.1 Implementation):**
```
API Startup:
1. Call services.DiscoverAndRegisterSlicerPlugins()
2. Scan assemblies for [SlicerPluginAttribute]
3. Find OrcaSlicer assembly with SlicerPlugin attribute
4. Instantiate OrcaSlicerLibrary_v2_3_1 and OrcaSlicerUIProvider_v2_3_1
5. Registry ready to serve profiles, assets, UI metadata

React Startup:
1. SlicerUIProvider wraps app
2. OrcaSlicer UI auto-registered in context
3. Pages use useSlicerUIRegistry() to load components dynamically
```

**Adding a Second Version (OrcaSlicer v2.9.4):**
```
Backend:
1. Create Farm.Slicers.OrcaSlicer.v2_9_x project
2. Add [SlicerPluginAttribute] to AssemblyInfo.cs
3. Add project reference to API
4. NO changes to ServiceCollectionExtensions.cs
5. Plugin discovery finds and registers both versions automatically

Frontend:
1. Import OrcaSlicerUI from v2.9.x library
2. Pages can now choose which version to use:
   - `slicerUIRegistry.getComponent("OrcaSlicer", "2.3.1", "import")`
   - `slicerUIRegistry.getComponent("OrcaSlicer", "2.9.4", "import")`
```

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
├── Week 1-2: Design ISlicerLibrary abstractions ✅ DONE
├── Week 3-4: Implement SlicerRegistry ✅ DONE
├── Week 5-8: Create Farm.Slicers.OrcaSlicer.v2_3_1 ✅ DONE
├── Week 9-10: Plugin discovery system ✅ DONE
└── Week 11-12: Migrate OrcaSlicer UI components ⏳ IN PROGRESS

Q2 2025
├── Week 1-4: Create Farm.Slicers.PrusaSlicer.v2_9_x ⏳ TODO
├── Week 5-8: Update frontend asset service ⏳ TODO
└── Week 9-12: Testing, documentation, production release ⏳ TODO
```

### 12. Files Created/Migrated

### Backend (C# / NuGet)
- `src/api/Program.cs` - Register slicer libraries via `.AddOrcaSlicerLibrary()` etc.
- `src/api/Interfaces/ISlicerUIProvider.cs` - New interface for UI metadata
- `src/api/Interfaces/ISlicerRegistry.cs` - Update to include UI provider lookup
- `src/api/Controllers/Slicing/ProfilesController.cs` - Refactor to use `ISlicerRegistry`
- `src/api/Services/Slicing/AssetService.cs` - Refactor to use `ISlicerAssetRegistry`
- `src/Slicers/Farm.Slicers.OrcaSlicer.v2_3_1/` - New library package
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

## Implementation Status Summary

### ✅ Completed
- Assembly-level plugin attributes for slicer library declaration (`SlicerPluginAttribute`)
- Plugin discovery system with reflection-based auto-registration (`SlicerPluginDiscovery`)
- Backend library abstractions (`ISlicerLibrary`, `ISlicerProfilesProvider`, `ISlicerAssetRegistry`, `ISlicerUIProvider`)
- Frontend registry service (`ISlicerUIRegistry`, `SlicerUIRegistry`)
- OrcaSlicer v2.3.1 library structure and core implementations (backend)
- OrcaSlicer v2.3.1 UI components migrated and registered (OrcaImportWizard, services, types)
- SlicerUIContext and React hooks for accessing UI registry
- Integration of plugin discovery into API startup (zero-config registration)
- SlicerUIProvider auto-registration of all slicer UI libraries on mount
- Path aliases for slicer libraries (`@farm/slicers-*` in tsconfig)
- Removal of old OrcaSlicer components from core app (cleanroom migration)
- PrusaSlicer v2.9.x library structure with plugin declaration
- PrusaSlicer v2.9.x UI stub components (ready for implementation)
- Registration of both OrcaSlicer and PrusaSlicer with auto-discovery

### ✅ Verification Status
- API build: ✅ SUCCESS (no errors)
- React linting: ✅ PASSED (0 errors)
- Plugin discovery: ✅ FUNCTIONAL (both slicers registered)
- Path aliases: ✅ WORKING (imports resolve correctly)

### ⏳ In Progress
- Complete PrusaSlicer UI component implementation
- Testing multi-slicer plugin discovery at runtime

### 📋 TODO
- Refactoring core API services to use `ISlicerRegistry`
- Refactoring core React pages to use `ISlicerUIRegistry` for dynamic component loading
- Comprehensive end-to-end testing with multiple slicer versions
- Comprehensive documentation for adding new slicer versions (quick-start guide)
- Create additional slicer libraries (Creality, Bambu, etc.)
- Performance optimization for lazy-loading slicer UI

## Questions for Team Review

**Architecture** ✅ Resolved with Plugin System
1. ✅ How to register new slicer versions without hardcoding?
   - **Solution**: Plugin discovery via `SlicerPluginAttribute` + assembly scanning
   - **Benefit**: New slicer versions auto-discovered at startup; zero changes to ServiceCollectionExtensions

2. ✅ Should backend library (NuGet) and frontend library (npm) be separate?
   - **Solution**: Keep in same directory; export both from same source (backend types + React UI)

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

## Quick-Start Guide: Adding a New Slicer Version

This section provides step-by-step instructions for adding a new slicer library (e.g., PrusaSlicer 3.0 or Creality Slicer).

### Prerequisites
- Understanding of C# and TypeScript
- Familiarity with slicer profile format and asset structure
- Slicer's official profile JSON and asset manifest documentation

### Step 1: Create Backend Library Structure

```bash
mkdir -p src/Slicers/Farm.Slicers.YourSlicer.vX_X_x/{lib,ui/{components,services,types}}
```

### Step 2: Create .csproj File

Create `src/Slicers/Farm.Slicers.YourSlicer.vX_X_x/Farm.Slicers.YourSlicer.vX_X_x.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <IsPackable>true</IsPackable>
    <PackageId>Farm.Slicers.YourSlicer.vX_X_x</PackageId>
    <Version>1.0.0</Version>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../../shared/Farm.Web.Shared.csproj" />
  </ItemGroup>

  <ItemGroup>
    <EmbeddedResource Include="lib/Profiles/official-profiles.json" Link="Resources/official-profiles.json" />
    <EmbeddedResource Include="lib/Assets/manifest.json" Link="Resources/manifest.json" />
  </ItemGroup>
</Project>
```

### Step 3: Implement Backend Classes

**`lib/YourSlicerLibrary_vX_X_x.cs`**:
```csharp
using Farm.Web.Shared.Contracts.Slicing.Libraries;

public class YourSlicerLibrary_vX_X_x : ISlicerLibrary
{
    public string Name => "YourSlicer";
    public string Version => "X.X.x";
    public string DisplayName => "YourSlicer X.X.x";

    public ISlicerProfilesProvider ProfilesProvider => new YourSlicerProfilesProvider();
    public ISlicerAssetRegistry AssetRegistry => new YourSlicerAssetRegistry();
}
```

**`lib/YourSlicerUIProvider_vX_X_x.cs`**:
```csharp
using Farm.Web.Shared.Contracts.Slicing.Libraries;

public class YourSlicerUIProvider_vX_X_x : ISlicerUIProvider
{
    public string SlicerName => "YourSlicer";
    public string Version => "X.X.x";
    public bool HasImportUI => true;
    public bool HasSettingsUI => false;
    public bool HasProfileEditorUI => false;
    public string? GetImportUIPath => "import-yourslicer";
    public string? GetSettingsUIPath => null;
    public string? GetProfileEditorUIPath => null;
}
```

**`lib/YourSlicerProfilesProvider.cs`** and **`lib/YourSlicerAssetRegistry.cs`**: Implement similar to OrcaSlicer versions (see existing implementations as templates).

### Step 4: Create AssemblyInfo.cs with Plugin Declaration

**`AssemblyInfo.cs`**:
```csharp
using Farm.Web.Shared.Contracts.Slicing.Libraries;
using Farm.Slicers.YourSlicer.vX_X_x.lib;

[assembly: SlicerPlugin(typeof(YourSlicerLibrary_vX_X_x), typeof(YourSlicerUIProvider_vX_X_x))]
```

### Step 5: Create Embedded Resource Files

1. **`lib/Profiles/official-profiles.json`**: Export official profiles from slicer
2. **`lib/Assets/manifest.json`**: Create manifest listing bed models, textures, cover images

```json
{
  "beds": [],
  "textures": [],
  "covers": [],
  "version": "X.X.x",
  "metadata": {
    "description": "YourSlicer X.X.x assets",
    "lastUpdated": "2025-01-01T00:00:00Z"
  }
}
```

### Step 6: Create Frontend UI Components

**`ui/components/YourImportWizard.tsx`**:
```typescript
import React from 'react';

export const YourImportWizard: React.FC = () => {
  // Implement import wizard (see OrcaImportWizard for template)
  return <div>YourSlicer Import Wizard</div>;
};
```

**`ui/services/yourProfilesService.ts`**:
```typescript
export const yourProfilesService = {
  async previewBundle(bundleJson: string) { /* ... */ },
  async importBundle(request) { /* ... */ },
  async exportBundle() { /* ... */ },
};
```

**`ui/types/yourProfiles.ts`**:
```typescript
export interface YourPrinterPreset { /* ... */ }
export interface YourMaterialPreset { /* ... */ }
export interface YourBundlePreview { /* ... */ }
```

### Step 7: Create UI Index Export

**`ui/index.ts`**:
```typescript
export { YourImportWizard } from './components/YourImportWizard';
export { yourProfilesService } from './services/yourProfilesService';
export type { YourPrinterPreset, YourMaterialPreset, YourBundlePreview } from './types/yourProfiles';
```

### Step 8: Add React Path Alias

Update `src/Web/ReactApp/tsconfig.paths.json`:

```json
"@farm/slicers-yourslicer-vx_x_x/*": ["../../Slicers/Farm.Slicers.YourSlicer.vX_X_x/ui/*"],
"@farm/slicers-yourslicer-vx_x_x": ["../../Slicers/Farm.Slicers.YourSlicer.vX_X_x/ui/index.ts"]
```

### Step 9: Register Slicer UI

Update `src/Web/ReactApp/src/services/slicer-registry/registerSlicerUI.ts`:

```typescript
export function registerYourSlicerUI(registry: ISlicerUIRegistry): void {
  import('@farm/slicers-yourslicer-vx_x_x').then((module) => {
    const yourExports: SlicerUIExports = {
      slicerName: 'YourSlicer',
      slicerVersion: 'X.X.x',
      ImportComponent: module.YourImportWizard,
      profilesService: module.yourProfilesService,
      types: {},
    };

    registry.registerUI('YourSlicer', 'X.X.x', yourExports);
    console.info('[registerSlicerUI] Registered YourSlicer vX.X.x');
  }).catch((err) => {
    console.error('[registerSlicerUI] Failed to register YourSlicer:', err);
  });
}

export function registerAllSlicerUI(registry: ISlicerUIRegistry): void {
  registerOrcaSlicerUI(registry);
  registerPrusaSlicerUI(registry);
  registerYourSlicerUI(registry);  // ← Add this line
}
```

### Step 10: Verify Setup

1. **API Build**: `cd src && dotnet build ./farm-web.sln -c Debug`
   - Should succeed with no errors
   - Check that plugin is discovered at startup

2. **React Linting**: `cd src/Web/ReactApp && npm run lint`
   - Should pass with 0 errors
   - Path aliases should resolve correctly

3. **Test with Multiple Slicers**:
   - Verify both OrcaSlicer and YourSlicer UI are registered
   - Check browser console for registration messages
   - Test import wizard loads correctly for each slicer

### Complete Example: PrusaSlicer v2.9.x

See `src/Slicers/Farm.Slicers.PrusaSlicer.v2_9_x/` for a full working example with:
- Backend library implementation
- Stub UI components (ready for real implementation)
- Proper plugin declaration
- Asset and profile resources

---

**Document Version**: 2.1  
**Last Updated**: 2025-11-11  
**Status**: Plugin Architecture Complete ✅ | Multi-Slicer Support Active ✅  
**Next Phase**: Dynamic Component Loading & End-to-End Testing

