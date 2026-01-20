/**
 * Type definitions for G-code file management
 * Consolidated from scattered definitions across the gcode feature
 */

/**
 * Represents a G-code file in the viewer context (simplified from ModelsPage)
 */
export interface GCodeFile {
  id: string;
  name: string;
  url: string;
  printTime?: number;
  filamentUsed?: number;
  layerCount?: number;
}

/**
 * File entry for file browser/explorer
 */
export interface FileEntry {
  id: string;
  name: string;
  path: string;
  isDirectory: boolean;
  size?: number;
  modifiedAt?: string;
  thumbnailUrl?: string;
}

/**
 * Represents a discovered file during harvest operations with progress tracking
 */
export interface FileWithProgress {
  id: string;
  harvestOperationId: string;
  printerPath: string;
  fileName: string;
  fileSizeBytes: number;
  modifiedAt?: string;
  fileHash?: string;
  isSelected?: boolean;
  alreadyInLibrary: boolean;
  existingLibraryFileId?: string;
  processingFailed: boolean;
  errorMessage?: string;
  thumbnailUrl?: string;
  extractedSlicerName?: string;
  extractedSlicerVersion?: string;
  extractedPrintTime?: number;
  extractedFilamentLength?: number;
  extractedNozzleDiameter?: number;
  extractedMaterial?: string;
  extractedLayerHeight?: string;
  extractedInfill?: string;
  // Progress tracking fields
  status?: string;
  error?: string;
  filePath?: string;
  progress?: number;
}

/**
 * File import status during harvest operations
 */
export interface FileImportStatus {
  fileId: string;
  fileName: string;
  status: 'pending' | 'importing' | 'success' | 'error';
  error?: string;
}

/**
 * Discovered file structure for harvest wizard
 */
export interface HarvestDiscoveredFile {
  id: string;
  fileName: string;
  filePath: string;
  fileSize: number;
  isSelected: boolean;
  alreadyInLibrary: boolean;
  thumbnailUrl?: string;
  extractedMetadata?: {
    slicerName?: string;
    printTime?: number;
    filamentUsed?: number;
  };
}

/**
 * Harvest options configuration
 */
export interface HarvestOptions {
  includeSubdirectories: boolean;
  skipExisting: boolean;
  autoSelectNew: boolean;
  extractThumbnails: boolean;
}

/**
 * Harvest wizard state
 */
export interface HarvestWizardState {
  step: number;
  selectedPrinters: string[];
  discoveredFiles: HarvestDiscoveredFile[];
  options: HarvestOptions;
  importStatus: FileImportStatus[];
}

/**
 * Folder node structure for hierarchical file browser
 */
export interface FolderNode {
  path: string;
  name: string;
  children: FolderNode[];
  fileCount: number;
  expanded: boolean;
}
