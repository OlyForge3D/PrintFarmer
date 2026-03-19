import { apiClient } from './api';

/**
 * Task status enum matching backend UserTaskStatus
 */
export enum TaskStatus {
  Pending = 'Pending',
  InProgress = 'InProgress',
  Completed = 'Completed',
  Dismissed = 'Dismissed',
  Skipped = 'Skipped'
}

/**
 * Task type enum matching backend UserTaskType
 */
export enum TaskType {
  ProfileImport = 'ProfileImport',
  MaintenanceDue = 'MaintenanceDue',
  FirmwareUpdate = 'FirmwareUpdate',
  CalibrationNeeded = 'CalibrationNeeded'
}

/**
 * Task priority enum matching backend UserTaskPriority
 */
export enum TaskPriority {
  Low = 'Low',
  Normal = 'Normal',
  High = 'High'
}

/**
 * User task DTO from the API
 */
export interface UserTask {
  id: string;
  taskType: TaskType;
  entityType: string;
  entityId: string;
  title: string;
  description?: string;
  status: TaskStatus;
  priority: TaskPriority;
  createdAt: string;
  dueAt?: string;
  completedAt?: string;
  relatedEntityCount: number;
  metadataJson?: string;
}

/**
 * Pending tasks count response
 */
export interface TaskCountResponse {
  count: number;
}

/**
 * DTO for creating a new task
 */
export interface CreateTaskDto {
  title: string;
  description?: string;
  priority?: TaskPriority;
}

/**
 * Tasks API service for managing user tasks
 */
export const tasksApi = {
  /**
   * Get all pending tasks
   */
  async getPendingTasks(): Promise<UserTask[]> {
    const response = await apiClient.get<UserTask[]>('/tasks');
    return response.data;
  },

  /**
   * Get task by ID
   */
  async getTask(taskId: string): Promise<UserTask> {
    const response = await apiClient.get<UserTask>(`/tasks/${taskId}`);
    return response.data;
  },

  /**
   * Get pending task count
   */
  async getPendingCount(): Promise<number> {
    const response = await apiClient.get<TaskCountResponse>('/tasks/count');
    return response.data.count;
  },

  /**
   * Complete a task
   */
  async completeTask(taskId: string): Promise<void> {
    await apiClient.post(`/tasks/${taskId}/complete`);
  },

  /**
   * Dismiss a task (hide but can be shown again)
   */
  async dismissTask(taskId: string): Promise<void> {
    await apiClient.post(`/tasks/${taskId}/dismiss`);
  },

  /**
   * Skip a task (won't be shown again)
   */
  async skipTask(taskId: string): Promise<void> {
    await apiClient.post(`/tasks/${taskId}/skip`);
  },

  /**
   * Create a new task
   */
  async createTask(dto: CreateTaskDto): Promise<UserTask> {
    const response = await apiClient.post<UserTask>('/tasks', dto);
    return response.data;
  }
};
