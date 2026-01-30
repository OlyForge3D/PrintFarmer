# OrcaSlicer Import Wizard Flow Diagram

## User Journey

```
┌─────────────────────────────────────────────────────────────┐
│                     ORCA IMPORT WIZARD                      │
│                   /profiles/import/orca                     │
│                    (Admin Access Only)                      │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│ STEP 1: UPLOAD                                              │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  ┌──────────────────────────────────────────────┐           │
│  │   📄                                         │           │
│  │   Click to select bundle file                │           │
│  │   Supports OrcaSlicer config bundle JSON     │           │
│  └──────────────────────────────────────────────┘           │
│                                                             │
│  User uploads config_bundle.json                            │
│                                                             │
│  [Preview Bundle] ───────────────────┐                      │
│                                      │                      │
└──────────────────────────────────────┼──────────────────────┘
                                       │
                                       ▼
                            POST /api/slicer/profiles/
                                 import/orca/preview
                                       │
                                       ▼
                            OrcaBundleParsingService
                              - Parse JSON sections
                              - Extract metadata
                              - Validate structure
                                       │
                                       ▼
┌──────────────────────────────────────┴───────────────────────┐
│ STEP 2: PREVIEW & SELECTION                                  │
├──────────────────────────────────────────────────────────────┤
│                                                              │
│  Summary Cards:                                              │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐                    │
│  │  8       │  │  12      │  │  6       │                    │
│  │ Printers │  │Filaments │  │Processes │                    │
│  └──────────┘  └──────────┘  └──────────┘                    │
│                                                              │
│  ☑ Printer Presets (8)                                       │
│    ☑ Bambu Lab X1 Carbon                                     │
│      Bambu Lab • 256x256x256mm • 0.4mm nozzle                │
│    ☑ Prusa MK4                                               │
│      Prusa Research • 250x210x220mm • 0.4mm nozzle           │
│    ...                                                       │
│                                                              │
│  ☑ Filament Presets (12)                                     │
│    ☑ Generic PLA                                             │
│      PLA • 210°C nozzle • 60°C bed                           │
│    ☑ PolyLite PETG                                           │
│      PETG • 240°C nozzle • 80°C bed                          │
│    ...                                                       │
│                                                              │
│  ☑ Process Presets (6)                                       │
│    ☑ 0.20mm Standard                                         │
│      0.2mm layer • 15% infill • standard quality             │
│    ☑ 0.12mm Fine                                             │
│      0.12mm layer • 15% infill • fine quality                │
│    ...                                                       │
│                                                              │
│  [← Back]                      [Import Selected →]           │
│                                        │                     │
└────────────────────────────────────────┼─────────────────────┘
                                         │
                                         ▼
                          POST /api/slicer/profiles/
                                import/orca
                          {
                            bundleJson: "...",
                            importPrinters: true,
                            importFilaments: true,
                            importProcesses: true
                          }
                                         │
                                         ▼
                          OrcaPresetMappingService
                            - Fuzzy match presets
                            - Calculate confidence
                            - Map to catalog entities
                                         │
                                         ▼
                          Persist to Database
                            - PrinterModels
                            - FilamentTypes
                            - ProcessProfiles
                                         │
                                         ▼
┌────────────────────────────────────────┴─────────────────────┐
│ STEP 3: COMPLETION                                           │
├──────────────────────────────────────────────────────────────┤
│                                                              │
│                    ✓                                         │
│              Import Complete!                                │
│    Your OrcaSlicer presets have been successfully imported.  │
│                                                              │
│  ┌──────────────────────────────────────────────┐            │
│  │        8         12         6                │            │
│  │     Printers  Filaments  Processes           │            │
│  └──────────────────────────────────────────────┘            │
│                                                              │
│  [Import Another Bundle]   [View Profiles]                   │
│                                                              │
└──────────────────────────────────────────────────────────────┘
```

## State Flow Diagram

```
┌──────────┐
│  upload  │
└────┬─────┘
     │ File selected
     │ User clicks "Preview Bundle"
     ▼
┌──────────┐
│ preview  │ ◄─── User can go back to upload
└────┬─────┘
     │ Presets selected
     │ User clicks "Import Selected"
     ▼
┌──────────┐
│ complete │
└────┬─────┘
     │ User clicks "Import Another" OR "View Profiles"
     ▼
┌──────────┐        ┌──────────────┐
│  upload  │   OR   │ /profiles    │
│  (reset) │        │ (navigate)   │
└──────────┘        └──────────────┘
```

## Component Hierarchy

```
<OrcaImportWizard>
│
├── renderStepIndicator()
│   ├── Step 1: Upload
│   ├── Step 2: Preview
│   ├── Step 3: Review (merged with Preview)
│   └── Step 4: Import (merged with Complete)
│
├── renderUploadStep()
│   ├── File input (hidden)
│   ├── Upload zone (drag-drop)
│   ├── Preview button
│   └── Error alert (if parsing fails)
│
├── renderPreviewStep()
│   ├── Summary cards (3 columns)
│   │   ├── Printers card (blue)
│   │   ├── Filaments card (green)
│   │   └── Processes card (purple)
│   │
│   ├── Printer presets section
│   │   ├── "Select All" checkbox
│   │   └── Individual preset checkboxes
│   │       └── Preset metadata (name, manufacturer, dimensions)
│   │
│   ├── Filament presets section
│   │   ├── "Select All" checkbox
│   │   └── Individual preset checkboxes
│   │       └── Preset metadata (name, material, temperatures)
│   │
│   ├── Process presets section
│   │   ├── "Select All" checkbox
│   │   └── Individual preset checkboxes
│   │       └── Preset metadata (name, layer height, infill)
│   │
│   ├── Navigation buttons
│   │   ├── Back button
│   │   └── Import button (with loading state)
│   │
│   └── Error alert (if import fails)
│
└── renderCompleteStep()
    ├── Success icon (CheckCircle)
    ├── Success message
    ├── Import statistics (3 columns)
    └── Action buttons
        ├── Import another bundle
        └── View profiles
```

## Data Flow

```
User Input ──────────────┐
                         │
                         ▼
           ┌─────────────────────────┐
           │  File Reader API        │
           │  (Browser)              │
           └────────┬────────────────┘
                    │ bundleJson: string
                    ▼
           ┌─────────────────────────┐
           │  previewMutation        │
           │  (React Query)          │
           └────────┬────────────────┘
                    │ HTTP POST
                    ▼
           ┌─────────────────────────┐
           │  orcaProfilesService    │
           │  .previewBundle()       │
           └────────┬────────────────┘
                    │ Axios request
                    ▼
           ┌─────────────────────────┐
           │  ProfilesController     │
           │  .PreviewOrcaBundle()   │
           └────────┬────────────────┘
                    │
                    ▼
           ┌─────────────────────────┐
           │  OrcaBundleParsingService│
           │  .ParseBundle()         │
           └────────┬────────────────┘
                    │ OrcaBundlePreview
                    ▼
           ┌─────────────────────────┐
           │  React State            │
           │  setPreview(data)       │
           └────────┬────────────────┘
                    │
                    ▼
           ┌─────────────────────────┐
           │  UI Render              │
           │  (Preview Step)         │
           └─────────────────────────┘

Selection Changes ───────┐
                         │
                         ▼
           ┌─────────────────────────┐
           │  React State            │
           │  selectedPrinters       │
           │  selectedFilaments      │
           │  selectedProcesses      │
           └────────┬────────────────┘
                    │
                    ▼
           ┌─────────────────────────┐
           │  importMutation         │
           │  (React Query)          │
           └────────┬────────────────┘
                    │ HTTP POST
                    ▼
           ┌─────────────────────────┐
           │  orcaProfilesService    │
           │  .importBundle()        │
           └────────┬────────────────┘
                    │ Axios request
                    ▼
           ┌─────────────────────────┐
           │  ProfilesController     │
           │  .ImportOrcaBundle()    │
           └────────┬────────────────┘
                    │
                    ▼
           ┌─────────────────────────┐
           │  OrcaPresetMappingService│
           │  .Map*Preset()          │
           └────────┬────────────────┘
                    │ Fuzzy matching
                    ▼
           ┌─────────────────────────┐
           │  AppDbContext           │
           │  (EF Core)              │
           └────────┬────────────────┘
                    │ Database insert
                    ▼
           ┌─────────────────────────┐
           │  ImportOrcaBundleResult │
           └────────┬────────────────┘
                    │
                    ▼
           ┌─────────────────────────┐
           │  UI Render              │
           │  (Complete Step)        │
           └─────────────────────────┘
```

## Error Handling Flow

```
User uploads file
       │
       ▼
┌──────────────┐
│ Invalid JSON?│─── Yes ──► Show error: "Failed to parse bundle"
└──────┬───────┘
       │ No
       ▼
┌──────────────┐
│ Valid Orca   │
│ bundle       │
│ structure?   │─── No ───► Show error: "Invalid bundle format"
└──────┬───────┘
       │ Yes
       ▼
┌──────────────┐
│ Parse success│
└──────┬───────┘
       │
       ▼
User selects presets
       │
       ▼
┌──────────────┐
│ Any presets  │
│ selected?    │─── No ───► Disable import button
└──────┬───────┘
       │ Yes
       ▼
┌──────────────┐
│ Import       │
│ request      │
└──────┬───────┘
       │
       ▼
┌──────────────┐
│ Server       │
│ error?       │─── Yes ──► Show error: "Import failed"
└──────┬───────┘            with detailed message
       │ No
       ▼
┌──────────────┐
│ Success!     │
│ Show stats   │
└──────────────┘
```

## TypeScript Type Safety

```
File Upload
    │
    ▼
string (JSON content)
    │
    ▼
orcaProfilesService.previewBundle(bundleJson)
    │
    ▼
Promise<OrcaBundlePreview>
    │
    ├─► printers: OrcaPrinterPreset[]
    ├─► filaments: OrcaFilamentPreset[]
    └─► processes: OrcaProcessPreset[]
         │
         ▼
    React State (typed)
         │
         ▼
    UI Rendering (type-safe)
         │
         ▼
    User Selection
         │
         ▼
    Set<string> (preset names)
         │
         ▼
    ImportOrcaBundleRequest {
      bundleJson: string,
      importPrinters: boolean,
      importFilaments: boolean,
      importProcesses: boolean
    }
         │
         ▼
    orcaProfilesService.importBundle(request)
         │
         ▼
    Promise<ImportOrcaBundleResult>
         │
         ▼
    {
      printersImported: number,
      filamentsImported: number,
      processesImported: number,
      errors: string[]
    }
```

---

**Last Updated**: 2025-01-09 (Phase 6 Task 4 Complete)
