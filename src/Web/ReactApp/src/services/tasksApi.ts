import { apiClient } from './api';
import type { ApiError } from '@/types/api';

/**
 * Task status enum matching backend UserTaskStatus.
 * Backend serializes as PascalCase enum names via JsonStringEnumConverter.
 */
export enum TaskStatus {
  Pending = 'Pending',
  InProgress = 'InProgress',
  Completed = 'Completed',
  Dismissed = 'Dismissed',
  Skipped = 'Skipped'
}

/**
 * Task type enum matching backend UserTaskType.
 * Backend serializes as PascalCase enum names via JsonStringEnumConverter.
 *
 * Values ≤ Custom pre-date the shift-plan compiler (#713). Values ≥ FailureClear
 * were introduced by the compiler; older backends won't emit them. Unknown
 * strings from newer backends must be tolerated (see {@link isKnownTaskType}).
 */
export enum TaskType {
  ProfileImport = 'ProfileImport',
  MaintenanceDue = 'MaintenanceDue',
  FirmwareUpdate = 'FirmwareUpdate',
  CalibrationNeeded = 'CalibrationNeeded',
  Custom = 'Custom',
  // -- Shift-plan compiler task types (issue #713) --
  FailureClear = 'FailureClear',
  HarvestReady = 'HarvestReady',
  FilamentRunout = 'FilamentRunout',
  MaintenanceInIdleWindow = 'MaintenanceInIdleWindow',
  SpoolRestock = 'SpoolRestock',
  PrintedPartRestock = 'PrintedPartRestock',
}

/**
 * Set of TaskType values this client understands. Used to render a safe
 * generic row for future/unknown kinds instead of failing.
 */
const KNOWN_TASK_TYPES: ReadonlySet<string> = new Set(Object.values(TaskType));

export function isKnownTaskType(value: string | undefined | null): value is TaskType {
  return typeof value === 'string' && KNOWN_TASK_TYPES.has(value);
}

/**
 * Task priority enum matching backend UserTaskPriority.
 * Backend serializes as PascalCase enum names via JsonStringEnumConverter.
 */
export enum TaskPriority {
  Low = 'Low',
  Normal = 'Normal',
  High = 'High'
}

/**
 * Time-anchor bucket for a shift-plan task (issue #713).
 * Backend serializes as **lowercase camelCase strings** via a custom
 * `UserTaskAnchorKindJsonConverter`. Unknown/future values round-trip to
 * `Unspecified` per the backend contract.
 */
export enum UserTaskAnchorKind {
  Unspecified = 'unspecified',
  Now = 'now',
  At = 'at',
  Window = 'window',
  AnytimeToday = 'anytimeToday',
  /** Group label for the merged At+Window timeline segment in the shift-plan response. */
  Timeline = 'timeline',
}

const KNOWN_ANCHOR_KINDS: ReadonlySet<string> = new Set(Object.values(UserTaskAnchorKind));

/** Narrow an untrusted wire value to a known {@link UserTaskAnchorKind}, defaulting to Unspecified. */
export function normalizeAnchorKind(value: unknown): UserTaskAnchorKind {
  return typeof value === 'string' && KNOWN_ANCHOR_KINDS.has(value)
    ? (value as UserTaskAnchorKind)
    : UserTaskAnchorKind.Unspecified;
}

/**
 * Canonical source of a materialized shift-plan task (issue #713).
 * Backend serializes as **lowercase camelCase strings** via a custom
 * `UserTaskSourceKindJsonConverter`.
 */
export enum UserTaskSourceKind {
  Unspecified = 'unspecified',
  Attention = 'attention',
  FailureIncident = 'failureIncident',
  Harvest = 'harvest',
  FilamentCoverage = 'filamentCoverage',
  Maintenance = 'maintenance',
  SpoolReorder = 'spoolReorder',
  PrintedPartStock = 'printedPartStock',
}

const KNOWN_SOURCE_KINDS: ReadonlySet<string> = new Set(Object.values(UserTaskSourceKind));

/** Narrow an untrusted wire value to a known {@link UserTaskSourceKind}, defaulting to Unspecified. */
export function normalizeSourceKind(value: unknown): UserTaskSourceKind {
  return typeof value === 'string' && KNOWN_SOURCE_KINDS.has(value)
    ? (value as UserTaskSourceKind)
    : UserTaskSourceKind.Unspecified;
}

/**
 * User task DTO from the API. Mirrors backend `UserTaskDto`.
 *
 * Shift-plan fields (`anchorKind`, `anchorAtUtc`, `windowStartUtc`,
 * `windowEndUtc`, `sourceKind`, `sourceId`) are populated for all responses
 * from #713-and-later backends; legacy tasks materialize with `unspecified`
 * for both anchor and source kinds, which is safe to render.
 */
export interface UserTask {
  id: string;
  taskType: TaskType | string;
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
  // -- Shift-plan fields (issue #713) --
  anchorKind?: UserTaskAnchorKind;
  anchorAtUtc?: string | null;
  windowStartUtc?: string | null;
  windowEndUtc?: string | null;
  sourceKind?: UserTaskSourceKind;
  sourceId?: string | null;
}

/**
 * A group of tasks sharing the same anchor bucket in the shift-plan view.
 * The `anchorKind` on the group is one of `now`, `timeline`, or `anytimeToday`
 * (Timeline groups merged At+Window tasks; each task within retains its own
 * `anchorKind`).
 */
export interface ShiftPlanGroup {
  anchorKind: UserTaskAnchorKind;
  tasks: UserTask[];
}

/** Response payload for `GET /api/tasks?view=shift` (issue #713). */
export interface ShiftPlan {
  groups: ShiftPlanGroup[];
  generatedAt: string;
}

/**
 * Pending tasks count response.
 */
export interface TaskCountResponse {
  count: number;
}

/**
 * DTO for creating a new task.
 */
export interface CreateTaskDto {
  title: string;
  description?: string;
  priority?: TaskPriority;
}

/**
 * Result of {@link tasksApi.getShiftPlan}. When the shift-plan feature is
 * disabled server-side (404), the wrapper falls back to the flat list so
 * callers can still render pending tasks. `mode` disambiguates the two shapes.
 */
export type ShiftPlanResult =
  | { mode: 'shift'; plan: ShiftPlan }
  | { mode: 'flat'; tasks: UserTask[] };

/** Type guard for ApiError-like shapes returned by apiClient. */
function isApiError(error: unknown): error is ApiError {
  return (
    typeof error === 'object' &&
    error !== null &&
    'statusCode' in error &&
    typeof (error as { statusCode: unknown }).statusCode === 'number'
  );
}

function normalizeTask(raw: UserTask): UserTask {
  return {
    ...raw,
    anchorKind: normalizeAnchorKind(raw.anchorKind),
    sourceKind: normalizeSourceKind(raw.sourceKind),
  };
}

function normalizeGroup(group: ShiftPlanGroup): ShiftPlanGroup {
  return {
    anchorKind: normalizeAnchorKind(group.anchorKind),
    tasks: (group.tasks ?? []).map(normalizeTask),
  };
}

/**
 * Tasks API service for managing user tasks.
 */
export const tasksApi = {
  /**
   * Get all pending tasks (flat list, legacy contract).
   */
  async getPendingTasks(): Promise<UserTask[]> {
    const response = await apiClient.get<UserTask[]>('/tasks');
    return response.data.map(normalizeTask);
  },

  /**
   * Get the shift-plan view (issue #713): anchor-grouped, deterministically
   * ordered pending tasks. When the server returns 404 (shift-plan feature
   * disabled), gracefully falls back to the flat pending-tasks list so the
   * widget can still render.
   */
  async getShiftPlan(): Promise<ShiftPlanResult> {
    try {
      const response = await apiClient.get<ShiftPlan>('/tasks?view=shift');
      const plan = response.data;
      return {
        mode: 'shift',
        plan: {
          generatedAt: plan.generatedAt,
          groups: (plan.groups ?? []).map(normalizeGroup),
        },
      };
    } catch (error) {
      if (isApiError(error) && error.statusCode === 404) {
        const tasks = await this.getPendingTasks();
        return { mode: 'flat', tasks };
      }
      throw error;
    }
  },

  /**
   * Get task by ID.
   */
  async getTask(taskId: string): Promise<UserTask> {
    const response = await apiClient.get<UserTask>(`/tasks/${taskId}`);
    return normalizeTask(response.data);
  },

  /**
   * Get pending task count.
   */
  async getPendingCount(): Promise<number> {
    const response = await apiClient.get<TaskCountResponse>('/tasks/count');
    return response.data.count;
  },

  /**
   * Complete a task.
   */
  async completeTask(taskId: string): Promise<void> {
    await apiClient.post(`/tasks/${taskId}/complete`);
  },

  /**
   * Dismiss a task (hide but can be shown again).
   */
  async dismissTask(taskId: string): Promise<void> {
    await apiClient.post(`/tasks/${taskId}/dismiss`);
  },

  /**
   * Skip a task (won't be shown again).
   */
  async skipTask(taskId: string): Promise<void> {
    await apiClient.post(`/tasks/${taskId}/skip`);
  },

  /**
   * Create a new manual task.
   */
  async createTask(dto: CreateTaskDto): Promise<UserTask> {
    const response = await apiClient.post<UserTask>('/tasks', dto);
    return normalizeTask(response.data);
  }
};
