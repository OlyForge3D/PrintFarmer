/**
 * Maintenance Hooks Module
 */
export { useMaintenanceAlerts } from './useMaintenanceAlerts';
export type { UseMaintenanceAlertsOptions, UseMaintenanceAlertsResult } from './useMaintenanceAlerts';

export { useMaintenanceStats } from './useMaintenanceStats';
export type { 
  UseMaintenanceStatsOptions, 
  UseMaintenanceStatsResult,
  PrinterMaintenanceStatus,
  FleetMaintenanceStats 
} from './useMaintenanceStats';

export { useUpcomingMaintenance } from './useUpcomingMaintenance';
export type { 
  UseUpcomingMaintenanceOptions, 
  UseUpcomingMaintenanceResult,
  UpcomingMaintenanceTask 
} from './useUpcomingMaintenance';

export { useComponentMaintenance, COMPONENT_CATEGORIES } from './useComponentMaintenance';
export type { 
  UseComponentMaintenanceOptions, 
  UseComponentMaintenanceResult,
  ComponentMaintenanceData,
  ComponentReplacement 
} from './useComponentMaintenance';
