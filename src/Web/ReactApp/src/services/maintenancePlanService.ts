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
  CreateMaintenancePlanDto,
  UpdateMaintenancePlanDto,
  CreateMaintenanceTaskDto,
  UpdateMaintenanceTaskDto,
  AddTaskComponentDto,
  CreateMaintenanceComponentDto,
  UpdateMaintenanceComponentDto,
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
}

export const maintenancePlanService = new MaintenancePlanService();
