// Centralized global debug settings for PrintFarmer (imported by all UI components)
declare global {
  interface Window {
    PrintFarmerDebug?: {
      printerCard?: boolean;
      printerHistory?: boolean;
      printerRealtime?: boolean;
      printerBulkActions?: boolean;
      printerSelection?: boolean;
      printerDashboard?: boolean;
      expandablePrinterCard?: boolean;
      printerDiscoveryModal?: boolean;
      telemetrySettingsPage?: boolean;
      [key: string]: boolean | undefined;
    };
  }
}
export {};
