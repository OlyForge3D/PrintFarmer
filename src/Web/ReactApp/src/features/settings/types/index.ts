/** Response from GET /api/settings/farm */
export interface FarmSettingsResponse {
  electricityRatePerKwh: number;
  defaultMachineHourlyRate: number;
  averagePrinterWattage: number;
  canWrite: boolean;
  rowVersion: string | null;
  /** Controls browser slicer UI complexity. Defaults to 'Simple'. Added by backend in slicerMode feature. */
  slicerMode?: 'Simple' | 'Advanced';
  slicerMode: 'Simple' | 'Advanced';
}

/** Request body for PUT /api/settings/farm */
export interface UpdateFarmSettingsRequest {
  electricityRatePerKwh?: number;
  defaultMachineHourlyRate?: number;
  averagePrinterWattage?: number;
  rowVersion?: string | null;
  slicerMode?: 'Simple' | 'Advanced';
  slicerMode?: 'Simple' | 'Advanced';
}

/** Response from GET /api/settings/user */
export interface UserSettingsResponse {
  userId: string;
  theme: string;
  locale: string;
  itemsPerPage: number;
  defaultSlicerPreset: string | null;
  printablesUsername: string | null;
  rowVersion: string | null;
}

/** Request body for PUT /api/settings/user */
export interface UpdateUserSettingsRequest {
  theme?: string;
  locale?: string;
  itemsPerPage?: number;
  defaultSlicerPreset?: string | null;
  printablesUsername?: string | null;
  rowVersion?: string | null;
}
