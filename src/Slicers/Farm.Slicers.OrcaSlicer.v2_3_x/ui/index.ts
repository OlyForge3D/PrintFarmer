/**
 * OrcaSlicer v2.3.1 UI Export
 *
 * This file aggregates all OrcaSlicer-specific UI components, services, types, and hooks.
 * The React app imports this single export and registers it with the SlicerUIRegistry.
 */

// Re-export all OrcaSlicer-specific UI pieces
export const OrcaSlicerUI = {
  // Slicer metadata
  slicerName: 'OrcaSlicer',
  slicerVersion: '2.3.1',

  // UI Components (to be migrated from core)
  // - OrcaImportWizard: Multi-step bundle import component
  // - OrcaBundleExport: Export profiles as bundle
  // - OrcaSlicerSettings: Engine-specific settings component

  // Services (to be migrated from core)
  // - orcaProfilesService: Bundle import/export operations
  // - orcaAssetService: Bed texture/cover image management

  // Types (to be migrated from core)
  // - OrcaSlicer profile config types
  // - OrcaSlicer bundle types

  // Hooks (to be created)
  // - useOrcaProfiles: Manage OrcaSlicer profiles
  // - useOrcaBundles: Handle bundle import/export
};

export type OrcaSlicerUIType = typeof OrcaSlicerUI;
