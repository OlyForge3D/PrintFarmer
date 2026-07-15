export interface UpcomingMaintenanceQueryParams {
  lookaheadDays?: number;
  includeOverdue?: boolean;
  printerId?: string;
}

export const maintenanceQueryKeys = {
  upcomingMaintenance: (params?: UpcomingMaintenanceQueryParams) =>
    params === undefined
      ? (['upcoming-maintenance'] as const)
      : (['upcoming-maintenance', params] as const),
};
