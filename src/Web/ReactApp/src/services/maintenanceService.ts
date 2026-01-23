// ============================================================================
// Maintenance Service
// API client for printer maintenance management
// ============================================================================

import { apiClient } from './api';
import type {
  MaintenanceAlert,
  MaintenanceLog,
  MaintenanceSchedule,
  PrinterStatistics,
  FleetPrinterStatistics,
  AcknowledgeAlertRequest,
  DismissAlertRequest,
  ResolveAlertRequest,
  ResolveAlertResponse,
  CreateMaintenanceLogRequest,
  CreateMaintenanceScheduleRequest,
  UpdateMaintenanceScheduleRequest,
  UpdateMaintenanceModeRequest
} from '@/types/maintenance';

// Analytics response types
export interface MaintenanceTrendEntry {
  date: string;
  printerName: string;
  component: string | null;
  taskName: string;
  cost: number;
}

export interface ComponentLifespanEntry {
  component: string;
  avgLifespanHours: number;
  replacements: number;
}

export interface MaintenanceCostEntry {
  month: string;
  totalCost: number;
}

export interface PrinterUptimeEntry {
  printerName: string;
  printerId: string;
  uptimePercent: number;
  maintenanceCount: number;
  totalDowntimeMinutes: number;
}

/**
 * Service for managing printer maintenance alerts, logs, schedules, and statistics.
 * Provides methods for all maintenance-related API operations.
 */
export class MaintenanceService {
  // ==========================================================================
  // Analytics Endpoints
  // ==========================================================================

  /**
   * Gets maintenance trends within a date range.
   */
  async getTrends(startDate?: Date, endDate?: Date): Promise<Array<{ date: string; printer: string; component: string; action: string; cost: number }>> {
    const params = new URLSearchParams();
    if (startDate) params.append('startDate', startDate.toISOString());
    if (endDate) params.append('endDate', endDate.toISOString());

    const queryString = params.toString();
    const url = `/maintenance/analytics/trends${queryString ? `?${queryString}` : ''}`;
    
    const response = await apiClient.get<MaintenanceTrendEntry[]>(url);
    
    // Transform to match expected frontend format
    return response.data.map(t => ({
      date: t.date.split('T')[0], // Extract date portion
      printer: t.printerName,
      component: t.component ?? 'General',
      action: t.taskName,
      cost: t.cost
    }));
  }

  /**
   * Gets component lifespan analytics.
   */
  async getComponentLifespan(): Promise<Array<{ component: string; avgLifespanHours: number; replacements: number }>> {
    const response = await apiClient.get<ComponentLifespanEntry[]>('/maintenance/analytics/component-lifespan');
    return response.data;
  }

  /**
   * Gets monthly cost analysis.
   */
  async getCostAnalysis(months: number = 12): Promise<Array<{ month: string; cost: number }>> {
    const response = await apiClient.get<MaintenanceCostEntry[]>(`/maintenance/analytics/cost?months=${months}`);
    
    // Transform to match expected frontend format
    return response.data.map(c => ({
      month: c.month,
      cost: c.totalCost
    }));
  }

  /**
   * Gets printer uptime metrics.
   */
  async getPrinterUptime(): Promise<Array<{ printer: string; uptimePercent: number; maintenanceCount: number; totalDowntimeMinutes: number }>> {
    const response = await apiClient.get<PrinterUptimeEntry[]>('/maintenance/analytics/uptime');
    
    // Transform to match expected frontend format
    return response.data.map(u => ({
      printer: u.printerName,
      uptimePercent: u.uptimePercent,
      maintenanceCount: u.maintenanceCount,
      totalDowntimeMinutes: u.totalDowntimeMinutes
    }));
  }

  /**
   * Gets all active maintenance alerts across all printers.
   */
  async getAllAlerts(): Promise<MaintenanceAlert[]> {
    const response = await apiClient.get<MaintenanceAlert[]>('/maintenance/alerts');
    return response.data;
  }

  /**
   * Gets a specific maintenance alert by ID.
   * @param id - The alert ID
   */
  async getAlertById(id: string): Promise<MaintenanceAlert> {
    const response = await apiClient.get<MaintenanceAlert>(`/maintenance/alerts/${id}`);
    return response.data;
  }

  /**
   * Gets all maintenance alerts for a specific printer.
   * @param printerId - The printer ID
   */
  async getPrinterAlerts(printerId: string): Promise<MaintenanceAlert[]> {
    const response = await apiClient.get<MaintenanceAlert[]>(`/maintenance/printers/${printerId}/alerts`);
    return response.data;
  }

  /**
   * Acknowledges a maintenance alert (user has seen it).
   * @param id - The alert ID
   * @param request - Acknowledgement details
   */
  async acknowledgeAlert(id: string, request: AcknowledgeAlertRequest): Promise<MaintenanceAlert> {
    const response = await apiClient.post<MaintenanceAlert>(`/maintenance/alerts/${id}/acknowledge`, request);
    return response.data;
  }

  /**
   * Resolves a maintenance alert by logging the completed maintenance.
   * @param id - The alert ID
   * @param request - Maintenance log details
   */
  async resolveAlert(id: string, request: ResolveAlertRequest): Promise<ResolveAlertResponse> {
    const response = await apiClient.post<ResolveAlertResponse>(`/maintenance/alerts/${id}/resolve`, request);
    return response.data;
  }

  /**
   * Dismisses a maintenance alert (user chooses to ignore).
   * @param id - The alert ID
   * @param request - Dismissal details
   */
  async dismissAlert(id: string, request: DismissAlertRequest): Promise<MaintenanceAlert> {
    const response = await apiClient.post<MaintenanceAlert>(`/maintenance/alerts/${id}/dismiss`, request);
    return response.data;
  }

  // ============================================================================
  // Maintenance Logs
  // ============================================================================

  /**
   * Gets maintenance history for a specific printer.
   * @param printerId - The printer ID
   */
  async getPrinterMaintenanceLogs(printerId: string): Promise<MaintenanceLog[]> {
    const response = await apiClient.get<MaintenanceLog[]>(`/maintenance/printers/${printerId}/logs`);
    return response.data;
  }

  /**
   * Creates a new maintenance log entry (manual logging without alert).
   * @param request - The maintenance log details
   */
  async createMaintenanceLog(request: CreateMaintenanceLogRequest): Promise<MaintenanceLog> {
    const response = await apiClient.post<MaintenanceLog>('/maintenance/logs', request);
    return response.data;
  }

  // ============================================================================
  // Maintenance Schedules
  // ============================================================================

  /**
   * Gets all maintenance schedules.
   */
  async getAllSchedules(): Promise<MaintenanceSchedule[]> {
    const response = await apiClient.get<MaintenanceSchedule[]>('/maintenance/schedules');
    return response.data;
  }

  /**
   * Gets maintenance schedules for a specific printer (includes both printer-specific and model-wide).
   * @param printerId - The printer ID
   */
  async getPrinterSchedules(printerId: string): Promise<MaintenanceSchedule[]> {
    const response = await apiClient.get<MaintenanceSchedule[]>(`/maintenance/printers/${printerId}/schedules`);
    return response.data;
  }

  /**
   * Creates a new maintenance schedule.
   * @param request - The schedule details
   */
  async createSchedule(request: CreateMaintenanceScheduleRequest): Promise<MaintenanceSchedule> {
    const response = await apiClient.post<MaintenanceSchedule>('/maintenance/schedules', request);
    return response.data;
  }

  /**
   * Updates an existing maintenance schedule.
   * @param id - The schedule ID
   * @param request - The updated schedule details
   */
  async updateSchedule(id: string, request: UpdateMaintenanceScheduleRequest): Promise<MaintenanceSchedule> {
    const response = await apiClient.put<MaintenanceSchedule>(`/maintenance/schedules/${id}`, request);
    return response.data;
  }

  /**
   * Deletes a maintenance schedule.
   * @param id - The schedule ID
   */
  async deleteSchedule(id: string): Promise<void> {
    await apiClient.delete(`/maintenance/schedules/${id}`);
  }

  // ============================================================================
  // Printer Statistics
  // ============================================================================

  /**
   * Gets cumulative statistics for a specific printer.
   * @param printerId - The printer ID
   */
  async getPrinterStatistics(printerId: string): Promise<PrinterStatistics> {
    const response = await apiClient.get<PrinterStatistics>(`/maintenance/printers/${printerId}/statistics`);
    return response.data;
  }

  /**
   * Gets fleet-wide statistics for all printers with maintenance projections.
   * Includes days until next maintenance and next maintenance task.
   */
  async getFleetStatistics(): Promise<FleetPrinterStatistics[]> {
    const response = await apiClient.get<FleetPrinterStatistics[]>('/maintenance/statistics/fleet');
    return response.data;
  }

  // ============================================================================
  // Maintenance Mode
  // ============================================================================

  /**
   * Updates the maintenance mode status for a printer.
   * When in maintenance mode, the printer should not receive new print jobs.
   * @param printerId - The printer ID
   * @param request - The maintenance mode request
   */
  async updateMaintenanceMode(printerId: string, request: UpdateMaintenanceModeRequest): Promise<void> {
    await apiClient.put(`/maintenance/printers/${printerId}/mode`, request);
  }
}

// Export singleton instance
export const maintenanceService = new MaintenanceService();
