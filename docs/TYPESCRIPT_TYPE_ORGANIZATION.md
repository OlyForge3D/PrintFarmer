# TypeScript Type Organization Guide

## Overview

This document describes the organization of TypeScript type definitions in the React application. All types are now consolidated into feature-based type files to eliminate duplication and improve discoverability.

## Type File Structure

All type files are located in `src/Web/ReactApp/src/types/`:

### Core Type Files

| File | Purpose | Key Types |
|------|---------|-----------|
| `api.ts` | Backend API DTOs | Printer, GcodeFile, QueueJob, PrinterBackend, etc. |
| `models.ts` | 3D model management | Model, ModelTag, ModelListItem |
| `queue.ts` | Print queue management | JobStatus, JobAction, HistoryJob, ModelStats, JobDetails |
| `gcode.ts` | G-code file management | GCodeFile, FileEntry, HarvestDiscoveredFile, HarvestOptions |
| `slicer.ts` | Slicer configuration | MaterialType, SlicerSettingsDto, JobStatus, MaterialPreset |
| `admin.ts` | Admin interface | User, Role, SystemLog, TagOption, EditingTag |
| `components.ts` | Component props | 77+ component prop interfaces |

### Additional Type Files

| File | Purpose |
|------|---------|
| `worker.ts` | Worker/slicer worker types |
| `jobScheduling.ts` | Job scheduling types |
| `predictions.ts` | Prediction-related types |
| `SpoolmanSettings.ts` | Spoolman integration settings |
| `NetworkDiscoverySettings.ts` | Network discovery settings |
| `SettingInputType.ts` | Settings input type enums |

## Import Patterns

### Importing Feature Types

```typescript
// Models feature
import type { Model, ModelTag } from '@/types/models';

// Queue feature
import type { JobStatus, JobAction, HistoryJob } from '@/types/queue';

// G-code feature
import type { GCodeFile, FileEntry } from '@/types/gcode';

// Slicer feature
import type { MaterialType, SlicerSettingsDto } from '@/types/slicer';

// Admin feature
import type { User, Role, SystemLog } from '@/types/admin';
```

### Importing Component Props

```typescript
import type { ModelGridViewProps, ModelListViewProps } from '@/types/components';
```

### Importing API Types

```typescript
import type { Printer, GcodeFile, QueueJob } from '@/types/api';
```

## Guidelines for Adding New Types

### 1. Determine the Correct File

Ask these questions:
- Is it a backend API DTO? → `api.ts`
- Is it related to 3D models? → `models.ts`
- Is it related to print queue? → `queue.ts`
- Is it related to G-code files? → `gcode.ts`
- Is it related to slicer configuration? → `slicer.ts`
- Is it related to admin features? → `admin.ts`
- Is it a component props interface? → `components.ts`
- Is it specific to a single component? → Keep it in the component file

### 2. Add Documentation

Always add JSDoc comments to explain the purpose of new types:

```typescript
/**
 * Represents a 3D model file in the system
 * This type is used throughout the models3d feature for displaying and managing 3D model files
 */
export interface Model {
  id: string;
  name: string;
  // ...
}
```

### 3. Export Properly

Always use named exports for types:

```typescript
// ✅ Good
export interface MyType { ... }
export type MyUnion = 'a' | 'b';

// ❌ Bad
export default interface MyType { ... }
```

### 4. Avoid Circular Imports

Type files should only import from:
- Other type files (if necessary)
- `api.ts` (for shared API types)

They should NOT import from:
- Component files
- Service files
- Utility files

## Migrating Old Code

When refactoring old code that has inline type definitions:

1. **Check if type already exists** in the appropriate type file
2. **Extract the type** to the correct file if it doesn't exist
3. **Update imports** to use the centralized type
4. **Remove duplicate definitions**
5. **Run linting and tests** to verify no issues

## Benefits of This Organization

### Single Source of Truth
- Each type is defined once in a logical location
- No more duplicate definitions across files

### Better Discoverability
- Developers know exactly where to look for types by feature
- Easy to browse available types in each domain

### Easier Maintenance
- Update a type in one place instead of hunting across multiple files
- Reduces risk of inconsistencies between duplicate definitions

### Cleaner Imports
- Consistent import patterns across the codebase
- Clear separation between feature types and API types

### Type Safety
- All component props properly typed
- No `any` types for known structures
- Better IDE autocomplete and error detection

## Common Patterns

### Component Props Pattern

Component props interfaces are stored in `components.ts` with descriptive names:

```typescript
// In components.ts
export interface ModelGridViewProps {
  models: Model[];
  isLoading: boolean;
  onViewerModel: (model: Model) => void;
  onTagModel: (model: Model) => void;
  formatFileSize: (bytes: number) => string;
}

// In component file
import type { Model } from '@/types/models';
import type { ModelGridViewProps } from '@/types/components';

export const ModelGridView: React.FC<ModelGridViewProps> = (props) => {
  // ...
};
```

### Enum and Union Types

Store related enums and union types together:

```typescript
// In queue.ts
export type JobStatus = 'queued' | 'printing' | 'paused' | 'completed' | 'failed';
export type JobAction = 'pause' | 'resume' | 'cancel' | 'priority';
```

### Complex Domain Types

For complex types with many fields, add detailed comments:

```typescript
/**
 * Detailed job information for the job details modal
 */
export interface JobDetails {
  id: string;
  name: string;
  status: string;
  priority: number;
  queuePosition: number;
  gcodeFileId: string;
  fileName?: string;
  printerId: string;
  printerName: string;
  printerModel: string;
  notes: string;
  tags: string[];
  materialType?: string;
  nozzleDiameter?: number;
  estimatedPrintTimeSeconds: number;
  estimatedFilamentUsage?: string;
  createdAt: string;
  queuedAt?: string;
  startedAt?: string;
  completedAt?: string;
}
```

## Troubleshooting

### "Cannot find type X"

1. Check if the type exists in one of the type files
2. Verify your import statement uses the correct path
3. Make sure you're using `type` imports: `import type { X } from '@/types/...';`

### "Circular dependency detected"

1. Check if your type file is importing from component/service files
2. Move shared types to `api.ts` or create a new dedicated type file
3. Verify the import graph doesn't create cycles

### "Type mismatch after consolidation"

1. Verify the consolidated type matches all previous usages
2. Check if any files have their own local type that should be removed
3. Run `npm run lint` and `npm run test:run` to catch issues

## Resources

- [TypeScript Handbook - Modules](https://www.typescriptlang.org/docs/handbook/modules.html)
- [React TypeScript Cheatsheet](https://react-typescript-cheatsheet.netlify.app/)
- [PrintFarmer Type Files](../../src/Web/ReactApp/src/types/)
