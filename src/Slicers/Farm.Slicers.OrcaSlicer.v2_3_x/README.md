# Farm.Slicers.OrcaSlicer.v2_3_x

OrcaSlicer v2.3.1 slicer library for PrintFarmer.

## Structure

```
.
├── lib/                           # C# backend library
│   ├── Core/
│   │   ├── OrcaSlicerLibrary.cs          # ISlicerLibrary implementation
│   │   └── OrcaSlicerUIProvider.cs       # ISlicerUIProvider implementation
│   ├── Profiles/
│   │   ├── OrcaSlicerProfilesProvider.cs # ISlicerProfilesProvider implementation
│   │   └── official-profiles.json        # Embedded OrcaSlicer profiles
│   └── Assets/
│       ├── OrcaSlicerAssetRegistry.cs    # ISlicerAssetRegistry implementation
│       └── manifest.json                 # Asset registry manifest
│
├── ui/                            # React TypeScript UI library
│   ├── components/                # React components (OrcaImportWizard, etc)
│   ├── services/                  # API clients (orcaProfilesService, etc)
│   ├── types/                     # TypeScript types (OrcaSlicer profile types)
│   ├── hooks/                     # React hooks (useOrcaProfiles, etc)
│   └── index.ts                   # Main UI export
│
├── package.json                   # npm package metadata
├── Farm.Slicers.OrcaSlicer.v2_3_x.csproj  # NuGet project file
└── README.md                      # This file
```

## Backend (C#)

### Implementing ISlicerLibrary

The `OrcaSlicerLibrary_v2_3_x` class implements `ISlicerLibrary` and serves as the entry point for the backend:

```csharp
var library = new OrcaSlicerLibrary_v2_3_x();
services.AddSlicerLibrary(library);
```

Features:
- **Profiles**: Access to OrcaSlicer official profiles via `ProfilesProvider`
- **Assets**: Bed textures, bed models, and printer cover images via `AssetRegistry`
- **Validation**: Configuration validation for OrcaSlicer-specific properties

### Embedded Resources

Official profiles and assets are embedded in the NuGet package as resources:
- `lib/Profiles/official-profiles.json` → Embedded profiles
- `lib/Assets/**/*` → Embedded bed models, textures, and cover images

## Frontend (React/TypeScript)

### UI Export

The `OrcaSlicerUI` object in `ui/index.ts` exports all OrcaSlicer-specific UI:

```typescript
import { OrcaSlicerUI } from '@farm/slicers-orcaslicer-v2_3_x/ui';

slicerUIRegistry.registerUI('OrcaSlicer', '2.3.1', OrcaSlicerUI);
```

### UI Components (To Be Migrated)

Components to migrate from core React app:
- `OrcaImportWizard` - Multi-step bundle import interface
- `OrcaBundleExport` - Export profiles as OrcaSlicer bundle
- `OrcaSlicerSettings` - Engine-specific settings UI

### Services (To Be Migrated)

Services to migrate from core:
- `orcaProfilesService` - Bundle import/export operations
- `orcaAssetService` - Manage bed textures and printer covers

### Types (To Be Migrated)

TypeScript types to migrate from core:
- OrcaSlicer profile configuration types
- OrcaSlicer bundle import/export types
- OrcaSlicer preset types

## Registration in App

### Backend (Program.cs)

```csharp
services
  .AddSlicerLibrary<OrcaSlicerLibrary_v2_3_x>()
  .AddSlicerUIProvider<OrcaSlicerUIProvider_v2_3_x>()
  .AddSlicerRegistry();
```

### Frontend (App.tsx)

```typescript
import { OrcaSlicerUI } from '@farm/slicers-orcaslicer-v2_3_x/ui';

const slicerUIRegistry = new SlicerUIRegistry();
slicerUIRegistry.registerUI('OrcaSlicer', '2.3.1', OrcaSlicerUI);
```

## Property Changes

When OrcaSlicer updates to a new version (e.g., 2.4.0), changes to profile schemas are handled entirely within this library:

1. Update profile types in `ui/types/`
2. Update UI components in `ui/components/`
3. Create new library: `Farm.Slicers.OrcaSlicer.v2_4_x`
4. No changes needed to core app or other slicers

## Future Versions

- `Farm.Slicers.OrcaSlicer.v2_4_x` - Next OrcaSlicer version
- `Farm.Slicers.OrcaSlicer.v2_5_x` - Planned future version
