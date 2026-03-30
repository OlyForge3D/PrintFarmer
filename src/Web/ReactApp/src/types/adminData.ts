// Types for admin data export/import functionality

export enum ImportMode {
  Merge = 0,
  Replace = 1,
}

export interface ImportStatistics {
  manufacturersImported: number;
  filamentTypesImported: number;
  printerModelsImported: number;
  hotendsImported: number;
  extrudersImported: number;
  toolheadsImported: number;
  nozzlesImported: number;
  locationsImported: number;
  printersImported: number;
  totalItemsImported: number;
  duration: string;
}

export interface ImportResponseDto {
  success: boolean;
  errors: string[];
  warnings: string[];
  statistics: ImportStatistics;
  completedAt: string;
}

export interface CatalogExportDto {
  manufacturers: unknown[];
  filamentTypes: unknown[];
  printerModels: unknown[];
  hotends: unknown[];
  extruders: unknown[];
  toolheads: unknown[];
  nozzles: unknown[];
  exportedAt: string;
}

export interface FullBackupExportDto {
  catalog: CatalogExportDto;
  printers: unknown[];
  locations: unknown[];
  exportedAt: string;
}

export interface ImportRequest {
  mode: ImportMode;
  backup?: FullBackupExportDto;
  catalog?: CatalogExportDto;
}

export interface ExportHistoryItem {
  timestamp: string;
  type: 'catalog' | 'printers' | 'full';
  filename: string;
}

// --- Catalog Update Types ---

export interface CatalogVersionDto {
  version: string | null;
  appliedAt: string | null;
  source: string | null;
}

export interface CatalogFileChange {
  fileName: string;
  category: string;
  changeType: string;
}

export interface CatalogUpdateCheckResult {
  updateAvailable: boolean;
  currentVersion: string | null;
  availableVersion: string | null;
  changedFiles: CatalogFileChange[];
  checkedAt: string;
  error: string | null;
}

export interface CatalogUpdateApplyResult {
  success: boolean;
  previousVersion: string | null;
  appliedVersion: string | null;
  updatedCategories: string[];
  appliedAt: string;
  error: string | null;
}
