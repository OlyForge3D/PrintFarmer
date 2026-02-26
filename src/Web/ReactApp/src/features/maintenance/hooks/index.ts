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

// Hierarchical plan hooks
export {
  useMaintenancePlans,
  useMaintenancePlan,
  usePlansForPrinter,
  useCreatePlan,
  useUpdatePlan,
  useDeletePlan,
  useCreateTask,
  useUpdateTask,
  useDeleteTask,
  useAddTaskComponent,
  useRemoveTaskComponent,
  planKeys,
} from './useMaintenancePlans';

// Parts inventory hooks
export {
  useMaintenanceComponents,
  useComponentCategories,
  useLowStockComponents,
  useCreateComponent,
  useUpdateComponent,
  useDeleteComponent,
  componentKeys,
} from './useMaintenanceComponents';

// Task catalog hooks (standalone tasks)
export {
  useTaskCatalog,
  useTaskCatalogItem,
  useTaskCategories,
  useCreateCatalogTask,
  useUpdateCatalogTask,
  useDeleteCatalogTask,
  useAddCatalogTaskComponent,
  useRemoveCatalogTaskComponent,
  taskCatalogKeys,
} from './useTaskCatalog';

// Schedule deployment hooks
export {
  useScheduleDeployments,
  useScheduleDeployment,
  useDeployPlan,
  useUpdateScheduleDeployment,
  useDeleteScheduleDeployment,
  scheduleKeys,
} from './useScheduleDeployments';
