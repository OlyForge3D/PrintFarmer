/** Response from GET /api/settings/farm */
export interface FarmSettingsResponse {
  electricityRatePerKwh: number;
  defaultMachineHourlyRate: number;
  averagePrinterWattage: number;
  canWrite: boolean;
  rowVersion: string | null;
}

/** Request body for PUT /api/settings/farm */
export interface UpdateFarmSettingsRequest {
  electricityRatePerKwh?: number;
  defaultMachineHourlyRate?: number;
  averagePrinterWattage?: number;
  rowVersion?: string | null;
}

/** Response from GET /api/settings/user */
export interface UserSettingsResponse {
  userId: string;
  theme: string;
  locale: string;
  itemsPerPage: number;
  defaultSlicerPreset: string | null;
  rowVersion: string | null;
}

/** Request body for PUT /api/settings/user */
export interface UpdateUserSettingsRequest {
  theme?: string;
  locale?: string;
  itemsPerPage?: number;
  defaultSlicerPreset?: string | null;
  rowVersion?: string | null;
}
