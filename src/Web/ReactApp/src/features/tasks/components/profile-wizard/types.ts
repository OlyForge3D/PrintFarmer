/**
 * Shared types for the Profile Import Wizard
 */

export interface MachineProfileDto {
  name: string;
  manufacturer: string;
  nozzleDiameter?: number;
  printerModel?: string;
  inherits?: string;
}

export interface FilamentProfileDto {
  name: string;
  manufacturer?: string;
  material: string;
  nozzleTemperature?: number;
  bedTemperature?: number;
  compatiblePrinters?: string[];
}

export interface ProcessProfileDto {
  name: string;
  manufacturer?: string;
  compatiblePrinters?: string[];
}

export interface PrinterModelDto {
  id: string;
  name: string;
  manufacturerId: string;
}

export interface ManufacturerDto {
  id: string;
  name: string;
}

// Wizard steps
export type WizardStep = 'model-select' | 'machine' | 'filaments' | 'review';

// Special filter values for the Printer column in filament step
export const PRINTER_FILTER_ALL = '(All)';
export const PRINTER_FILTER_TEMPLATES = '(Templates)';

// Special filter value for Type/Vendor columns
export const FILTER_ALL = '(All)';

/**
 * Extract vendor from a filament profile.
 * Handles "OrcaFilamentLibrary" manufacturer by parsing from profile name.
 */
export function getFilamentVendor(filament: FilamentProfileDto): string {
  let vendor = filament.manufacturer || 'Generic';

  // If manufacturer is library-style, extract vendor from profile name
  if (vendor.toLowerCase().includes('library') || vendor.toLowerCase().includes('orca')) {
    const nameParts = filament.name.split(' ');
    if (nameParts.length > 1) {
      vendor = nameParts[0];
    } else {
      vendor = 'Generic';
    }
  }

  return vendor;
}

/**
 * Response containing names of already-imported profiles for a printer model.
 * Used by the import wizard to pre-check already-imported profiles.
 */
export interface ImportedProfileNamesDto {
  machineProfileNames: string[];
  processProfileNames: string[];
  filamentProfileNames: string[];
}
