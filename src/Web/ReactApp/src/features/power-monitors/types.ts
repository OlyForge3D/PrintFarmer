/** Provider types for smart plug power monitors. */
export type PowerMonitorProvider = 'Kasa' | 'Tasmota' | 'Shelly' | 'HomeAssistant';

/** A power monitor entry linked to a printer via smart plug. */
export interface PowerMonitor {
  id: string;
  printerId: string;
  printerName?: string;
  provider: PowerMonitorProvider;
  deviceAddress: string;
  /** Per-printer electricity rate in USD/kWh. Overrides farm-wide fallback. */
  electricityRatePerKwh?: number;
  enabled: boolean;
  createdAt?: string;
  updatedAt?: string;
}

/** Payload to create or update a power monitor. */
export interface PowerMonitorUpsert {
  printerId: string;
  provider: PowerMonitorProvider;
  deviceAddress: string;
  electricityRatePerKwh?: number;
  enabled: boolean;
}

/** Result of testing connectivity to a smart plug device. */
export interface PowerMonitorTestResult {
  success: boolean;
  message?: string;
  currentWatts?: number;
}

/** Live reading from a power monitor. */
export interface PowerReading {
  printerId: string;
  watts: number;
  timestamp: string;
}

export const POWER_MONITOR_PROVIDERS: { value: PowerMonitorProvider; label: string }[] = [
  { value: 'Kasa', label: 'TP-Link Kasa' },
  { value: 'Tasmota', label: 'Tasmota' },
  { value: 'Shelly', label: 'Shelly' },
  { value: 'HomeAssistant', label: 'Home Assistant' },
];
