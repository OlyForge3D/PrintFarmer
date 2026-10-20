/**
 * Type definitions for slicer configuration and job management
 * Consolidated from scattered definitions across the slicer feature
 */

/**
 * Material/Filament type options
 */
export type MaterialType = 'PLA' | 'PETG' | 'ABS' | 'TPU' | 'Nylon' | 'Carbon' | 'Other';

/**
 * Material preset with temperature settings
 */
export interface MaterialPreset {
  name: MaterialType;
  nozzleTemp: number;
  bedTemp: number;
}

/**
 * Slicer settings configuration per engine
 */
export interface PerEngineSetting {
  path?: string | null;
  argsTemplate?: string | null;
}

/**
 * Slicer settings DTO
 */
export interface SlicerSettingsDto {
  enabled: boolean;
  perEngine: Record<string, PerEngineSetting>;
  jitterPercent?: number;
}

/**
 * Job status information
 */
export interface JobStatus {
  id: string;
  status: 'pending' | 'processing' | 'completed' | 'failed';
  progress?: number;
  message?: string;
  outputFile?: string;
  error?: string;
}

/**
 * Printer list item for printer selection
 */
export interface PrinterListItem {
  id: string;
  name: string;
  manufacturerName?: string;
  modelName?: string;
  backend?: string;
}

/**
 * Available profile information
 */
export interface AvailableProfile {
  id: string;
  name: string;
  type: 'machine' | 'process' | 'filament';
  manufacturer?: string;
  model?: string;
}
