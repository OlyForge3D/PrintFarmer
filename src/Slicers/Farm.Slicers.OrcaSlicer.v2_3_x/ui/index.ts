/**
 * OrcaSlicer v2.3.1 UI Export
 *
 * This file aggregates all OrcaSlicer-specific UI components, services, types, and hooks.
 * The React app imports these exports and registers them with the SlicerUIRegistry.
 */

// OrcaSlicer UI Components
export { OrcaImportWizard } from './components/OrcaImportWizard';

// OrcaSlicer UI Services
export { orcaProfilesService } from './services/orcaProfilesService';

// OrcaSlicer UI Types
export type {
  OrcaPrinterPreset,
  OrcaFilamentPreset,
  OrcaProcessPreset,
  OrcaBundlePreview,
  ImportOrcaBundleRequest,
  ImportOrcaBundleResult,
  ExportOrcaBundleRequest,
  PrinterPresetMatch,
  FilamentPresetMatch,
  ProcessPresetMatch,
  OrcaBundleMappingResult,
} from './types/orcaProfiles';
