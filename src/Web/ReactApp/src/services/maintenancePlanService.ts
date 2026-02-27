// ============================================================================
// Maintenance Plan Service
// API client for hierarchical maintenance plans, tasks, and components
// ============================================================================

import { apiClient } from './api';
import type {
  MaintenancePlanDto,
  MaintenanceTaskDto,
  MaintenanceTaskComponentDto,
  MaintenanceComponentDto,
  PrinterMaintenanceScheduleDto,
  CreateMaintenancePlanDto,
  UpdateMaintenancePlanDto,
  CreateMaintenanceTaskDto,
  UpdateMaintenanceTaskDto,
  AddTaskComponentDto,
  DeployMaintenancePlanDto,
  UpdateScheduleDeploymentDto,
  CreateMaintenanceComponentDto,
  UpdateMaintenanceComponentDto,
  MaintenanceExportEnvelope,
  MaintenanceImportResult,
} from '@/types/maintenance';

/**
 * Service for hierarchical maintenance plan management.
 * Plans → Tasks → Components (parts inventory).
 */
export class MaintenancePlanService {
  // ──────────────────────── Plans ────────────────────────

  async getPlans(activeOnly?: boolean): Promise<MaintenancePlanDto[]> {
    const params = activeOnly != null ? `?activeOnly=${activeOnly}` : '';
    const res = await apiClient.get<MaintenancePlanDto[]>(`/maintenance/plans${params}`);
    return res.data;
  }

  async getPlanById(id: string): Promise<MaintenancePlanDto> {
    const res = await apiClient.get<MaintenancePlanDto>(`/maintenance/plans/${id}`);
    return res.data;
  }

  async getPlansForPrinter(printerId: string): Promise<MaintenancePlanDto[]> {
    const res = await apiClient.get<MaintenancePlanDto[]>(`/maintenance/plans/for-printer/${printerId}`);
    return res.data;
  }

  async createPlan(data: CreateMaintenancePlanDto): Promise<MaintenancePlanDto> {
    const res = await apiClient.post<MaintenancePlanDto>('/maintenance/plans', data);
    return res.data;
  }

  async updatePlan(id: string, data: UpdateMaintenancePlanDto): Promise<MaintenancePlanDto> {
    const res = await apiClient.put<MaintenancePlanDto>(`/maintenance/plans/${id}`, data);
    return res.data;
  }

  async deletePlan(id: string): Promise<void> {
    await apiClient.delete(`/maintenance/plans/${id}`);
  }

  // ──────────────────────── Tasks ────────────────────────

  async getTasks(planId: string): Promise<MaintenanceTaskDto[]> {
    const res = await apiClient.get<MaintenanceTaskDto[]>(`/maintenance/plans/${planId}/tasks`);
    return res.data;
  }

  async getTask(planId: string, taskId: string): Promise<MaintenanceTaskDto> {
    const res = await apiClient.get<MaintenanceTaskDto>(`/maintenance/plans/${planId}/tasks/${taskId}`);
    return res.data;
  }

  async createTask(planId: string, data: CreateMaintenanceTaskDto): Promise<MaintenanceTaskDto> {
    const res = await apiClient.post<MaintenanceTaskDto>(`/maintenance/plans/${planId}/tasks`, data);
    return res.data;
  }

  async updateTask(planId: string, taskId: string, data: UpdateMaintenanceTaskDto): Promise<MaintenanceTaskDto> {
    const res = await apiClient.put<MaintenanceTaskDto>(`/maintenance/plans/${planId}/tasks/${taskId}`, data);
    return res.data;
  }

  async deleteTask(planId: string, taskId: string): Promise<void> {
    await apiClient.delete(`/maintenance/plans/${planId}/tasks/${taskId}`);
  }

  // ──────────────────── Task Components ──────────────────

  async getTaskComponents(planId: string, taskId: string): Promise<MaintenanceTaskComponentDto[]> {
    const res = await apiClient.get<MaintenanceTaskComponentDto[]>(
      `/maintenance/plans/${planId}/tasks/${taskId}/components`
    );
    return res.data;
  }

  async addTaskComponent(planId: string, taskId: string, data: AddTaskComponentDto): Promise<MaintenanceTaskComponentDto> {
    const res = await apiClient.post<MaintenanceTaskComponentDto>(
      `/maintenance/plans/${planId}/tasks/${taskId}/components`,
      data
    );
    return res.data;
  }

  async removeTaskComponent(planId: string, taskId: string, componentId: string): Promise<void> {
    await apiClient.delete(`/maintenance/plans/${planId}/tasks/${taskId}/components/${componentId}`);
  }

  // ──────────────── Components (Inventory) ───────────────

  async getComponents(category?: string): Promise<MaintenanceComponentDto[]> {
    const params = category ? `?category=${encodeURIComponent(category)}` : '';
    const res = await apiClient.get<MaintenanceComponentDto[]>(`/maintenance/components${params}`);
    return res.data;
  }

  async getComponentById(id: string): Promise<MaintenanceComponentDto> {
    const res = await apiClient.get<MaintenanceComponentDto>(`/maintenance/components/${id}`);
    return res.data;
  }

  async getCategories(): Promise<string[]> {
    const res = await apiClient.get<string[]>('/maintenance/components/categories');
    return res.data;
  }

  async getLowStock(): Promise<MaintenanceComponentDto[]> {
    const res = await apiClient.get<MaintenanceComponentDto[]>('/maintenance/components/low-stock');
    return res.data;
  }

  async createComponent(data: CreateMaintenanceComponentDto): Promise<MaintenanceComponentDto> {
    const res = await apiClient.post<MaintenanceComponentDto>('/maintenance/components', data);
    return res.data;
  }

  async updateComponent(id: string, data: UpdateMaintenanceComponentDto): Promise<MaintenanceComponentDto> {
    const res = await apiClient.put<MaintenanceComponentDto>(`/maintenance/components/${id}`, data);
    return res.data;
  }

  async deleteComponent(id: string): Promise<void> {
    await apiClient.delete(`/maintenance/components/${id}`);
  }

  // ──────────── Task Catalog (standalone) ────────────────

  async getCatalogTasks(category?: string, activeOnly?: boolean): Promise<MaintenanceTaskDto[]> {
    const params = new URLSearchParams();
    if (category) params.set('category', category);
    if (activeOnly != null) params.set('activeOnly', String(activeOnly));
    const qs = params.toString();
    const res = await apiClient.get<MaintenanceTaskDto[]>(`/maintenance/tasks${qs ? `?${qs}` : ''}`);
    return res.data;
  }

  async getCatalogTaskById(id: string): Promise<MaintenanceTaskDto> {
    const res = await apiClient.get<MaintenanceTaskDto>(`/maintenance/tasks/${id}`);
    return res.data;
  }

  async getTaskCategories(): Promise<string[]> {
    const res = await apiClient.get<string[]>('/maintenance/tasks/categories');
    return res.data;
  }

  async createCatalogTask(data: CreateMaintenanceTaskDto): Promise<MaintenanceTaskDto> {
    const res = await apiClient.post<MaintenanceTaskDto>('/maintenance/tasks', data);
    return res.data;
  }

  async updateCatalogTask(id: string, data: UpdateMaintenanceTaskDto): Promise<MaintenanceTaskDto> {
    const res = await apiClient.put<MaintenanceTaskDto>(`/maintenance/tasks/${id}`, data);
    return res.data;
  }

  async deleteCatalogTask(id: string): Promise<void> {
    await apiClient.delete(`/maintenance/tasks/${id}`);
  }

  async getCatalogTaskComponents(taskId: string): Promise<MaintenanceTaskComponentDto[]> {
    const res = await apiClient.get<MaintenanceTaskComponentDto[]>(`/maintenance/tasks/${taskId}/components`);
    return res.data;
  }

  async addCatalogTaskComponent(taskId: string, data: AddTaskComponentDto): Promise<MaintenanceTaskComponentDto> {
    const res = await apiClient.post<MaintenanceTaskComponentDto>(`/maintenance/tasks/${taskId}/components`, data);
    return res.data;
  }

  async removeCatalogTaskComponent(taskId: string, componentId: string): Promise<void> {
    await apiClient.delete(`/maintenance/tasks/${taskId}/components/${componentId}`);
  }

  // ─────────── Schedule Deployments ──────────────────────

  async getScheduleDeployments(printerId?: string, planId?: string, activeOnly?: boolean): Promise<PrinterMaintenanceScheduleDto[]> {
    const params = new URLSearchParams();
    if (printerId) params.set('printerId', printerId);
    if (planId) params.set('planId', planId);
    if (activeOnly != null) params.set('activeOnly', String(activeOnly));
    const qs = params.toString();
    const res = await apiClient.get<PrinterMaintenanceScheduleDto[]>(`/maintenance/schedules${qs ? `?${qs}` : ''}`);
    return res.data;
  }

  async getScheduleDeploymentById(id: string): Promise<PrinterMaintenanceScheduleDto> {
    const res = await apiClient.get<PrinterMaintenanceScheduleDto>(`/maintenance/schedules/${id}`);
    return res.data;
  }

  async deployPlan(data: DeployMaintenancePlanDto): Promise<PrinterMaintenanceScheduleDto> {
    const res = await apiClient.post<PrinterMaintenanceScheduleDto>('/maintenance/schedules', data);
    return res.data;
  }

  async updateScheduleDeployment(id: string, data: UpdateScheduleDeploymentDto): Promise<PrinterMaintenanceScheduleDto> {
    const res = await apiClient.put<PrinterMaintenanceScheduleDto>(`/maintenance/schedules/${id}`, data);
    return res.data;
  }

  async deleteScheduleDeployment(id: string): Promise<void> {
    await apiClient.delete(`/maintenance/schedules/${id}`);
  }

  // ──────────────────── Import / Export ────────────────────

  async exportTasks(): Promise<MaintenanceExportEnvelope> {
    const res = await apiClient.get<MaintenanceExportEnvelope>('/maintenance/tasks/export');
    return res.data;
  }

  async importTasks(envelope: MaintenanceExportEnvelope): Promise<MaintenanceImportResult> {
    const res = await apiClient.post<MaintenanceImportResult>('/maintenance/tasks/import', envelope);
    return res.data;
  }

  async exportComponents(): Promise<MaintenanceExportEnvelope> {
    const res = await apiClient.get<MaintenanceExportEnvelope>('/maintenance/components/export');
    return res.data;
  }

  async importComponents(envelope: MaintenanceExportEnvelope): Promise<MaintenanceImportResult> {
    const res = await apiClient.post<MaintenanceImportResult>('/maintenance/components/import', envelope);
    return res.data;
  }

  async exportPlans(): Promise<MaintenanceExportEnvelope> {
    const res = await apiClient.get<MaintenanceExportEnvelope>('/maintenance/plans/export');
    return res.data;
  }

  async importPlans(envelope: MaintenanceExportEnvelope): Promise<MaintenanceImportResult> {
    const res = await apiClient.post<MaintenanceImportResult>('/maintenance/plans/import', envelope);
    return res.data;
  }

  async exportBundle(): Promise<MaintenanceExportEnvelope> {
    const res = await apiClient.get<MaintenanceExportEnvelope>('/maintenance/export');
    return res.data;
  }

  async importBundle(envelope: MaintenanceExportEnvelope): Promise<MaintenanceImportResult> {
    const res = await apiClient.post<MaintenanceImportResult>('/maintenance/import', envelope);
    return res.data;
  }
}

export const maintenancePlanService = new MaintenancePlanService();
