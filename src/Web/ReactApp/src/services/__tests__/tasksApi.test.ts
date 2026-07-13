import { describe, it, expect, vi, beforeEach } from 'vitest';
import {
  tasksApi,
  TaskStatus,
  TaskType,
  TaskPriority,
  UserTaskAnchorKind,
  UserTaskSourceKind,
  normalizeAnchorKind,
  normalizeSourceKind,
  isKnownTaskType,
} from '../tasksApi';
import { apiClient } from '../api';

vi.mock('../api', () => ({
  apiClient: {
    get: vi.fn(),
    post: vi.fn(),
  },
}));

describe('tasksApi', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('getPendingTasks', () => {
    it('should get all pending tasks', async () => {
      const mockTasks = [
        {
          id: 'task-1',
          taskType: TaskType.ProfileImport,
          entityType: 'Printer',
          entityId: 'printer-1',
          title: 'Import Profiles',
          description: 'Import slicer profiles',
          status: TaskStatus.Pending,
          priority: TaskPriority.Normal,
          createdAt: '2024-01-01T00:00:00Z',
          relatedEntityCount: 1,
        },
        {
          id: 'task-2',
          taskType: TaskType.MaintenanceDue,
          entityType: 'Printer',
          entityId: 'printer-2',
          title: 'Maintenance Due',
          status: TaskStatus.InProgress,
          priority: TaskPriority.High,
          createdAt: '2024-01-02T00:00:00Z',
          dueAt: '2024-01-10T00:00:00Z',
          relatedEntityCount: 1,
        },
      ];

      vi.mocked(apiClient.get).mockResolvedValue({ data: mockTasks });

      const result = await tasksApi.getPendingTasks();

      expect(apiClient.get).toHaveBeenCalledWith('/tasks');
      expect(result).toEqual(
        mockTasks.map((t) => ({
          ...t,
          anchorKind: 'unspecified',
          sourceKind: 'unspecified',
        })),
      );
      expect(result).toHaveLength(2);
    });

    it('should return empty array when no tasks exist', async () => {
      vi.mocked(apiClient.get).mockResolvedValue({ data: [] });

      const result = await tasksApi.getPendingTasks();

      expect(result).toEqual([]);
    });
  });

  describe('getTask', () => {
    it('should get task by ID', async () => {
      const mockTask = {
        id: 'task-123',
        taskType: TaskType.FirmwareUpdate,
        entityType: 'Printer',
        entityId: 'printer-1',
        title: 'Firmware Update Available',
        description: 'New firmware version 1.2.3 is available',
        status: TaskStatus.Pending,
        priority: TaskPriority.Normal,
        createdAt: '2024-01-05T00:00:00Z',
        relatedEntityCount: 1,
        metadataJson: '{"version":"1.2.3"}',
      };

      vi.mocked(apiClient.get).mockResolvedValue({ data: mockTask });

      const result = await tasksApi.getTask('task-123');

      expect(apiClient.get).toHaveBeenCalledWith('/tasks/task-123');
      expect(result).toEqual({
        ...mockTask,
        anchorKind: 'unspecified',
        sourceKind: 'unspecified',
      });
    });
  });

  describe('getPendingCount', () => {
    it('should get pending task count', async () => {
      const mockResponse = { count: 5 };

      vi.mocked(apiClient.get).mockResolvedValue({ data: mockResponse });

      const result = await tasksApi.getPendingCount();

      expect(apiClient.get).toHaveBeenCalledWith('/tasks/count');
      expect(result).toBe(5);
    });

    it('should return zero when no pending tasks', async () => {
      const mockResponse = { count: 0 };

      vi.mocked(apiClient.get).mockResolvedValue({ data: mockResponse });

      const result = await tasksApi.getPendingCount();

      expect(result).toBe(0);
    });
  });

  describe('completeTask', () => {
    it('should complete a task', async () => {
      vi.mocked(apiClient.post).mockResolvedValue({});

      await tasksApi.completeTask('task-complete');

      expect(apiClient.post).toHaveBeenCalledWith('/tasks/task-complete/complete');
    });
  });

  describe('dismissTask', () => {
    it('should dismiss a task', async () => {
      vi.mocked(apiClient.post).mockResolvedValue({});

      await tasksApi.dismissTask('task-dismiss');

      expect(apiClient.post).toHaveBeenCalledWith('/tasks/task-dismiss/dismiss');
    });
  });

  describe('skipTask', () => {
    it('should skip a task', async () => {
      vi.mocked(apiClient.post).mockResolvedValue({});

      await tasksApi.skipTask('task-skip');

      expect(apiClient.post).toHaveBeenCalledWith('/tasks/task-skip/skip');
    });
  });

  describe('Enums', () => {
    it('should have TaskStatus enum values', () => {
      expect(TaskStatus.Pending).toBe('Pending');
      expect(TaskStatus.InProgress).toBe('InProgress');
      expect(TaskStatus.Completed).toBe('Completed');
      expect(TaskStatus.Dismissed).toBe('Dismissed');
      expect(TaskStatus.Skipped).toBe('Skipped');
    });

    it('should have TaskType enum values', () => {
      expect(TaskType.ProfileImport).toBe('ProfileImport');
      expect(TaskType.MaintenanceDue).toBe('MaintenanceDue');
      expect(TaskType.FirmwareUpdate).toBe('FirmwareUpdate');
      expect(TaskType.CalibrationNeeded).toBe('CalibrationNeeded');
      expect(TaskType.FailureClear).toBe('FailureClear');
      expect(TaskType.HarvestReady).toBe('HarvestReady');
      expect(TaskType.FilamentRunout).toBe('FilamentRunout');
      expect(TaskType.MaintenanceInIdleWindow).toBe('MaintenanceInIdleWindow');
      expect(TaskType.SpoolRestock).toBe('SpoolRestock');
      expect(TaskType.PrintedPartRestock).toBe('PrintedPartRestock');
      expect(TaskType.Custom).toBe('Custom');
    });

    it('should have TaskPriority enum values', () => {
      expect(TaskPriority.Low).toBe('Low');
      expect(TaskPriority.Normal).toBe('Normal');
      expect(TaskPriority.High).toBe('High');
    });

    it('should have UserTaskAnchorKind camelCase wire values', () => {
      expect(UserTaskAnchorKind.Unspecified).toBe('unspecified');
      expect(UserTaskAnchorKind.Now).toBe('now');
      expect(UserTaskAnchorKind.At).toBe('at');
      expect(UserTaskAnchorKind.Window).toBe('window');
      expect(UserTaskAnchorKind.AnytimeToday).toBe('anytimeToday');
      expect(UserTaskAnchorKind.Timeline).toBe('timeline');
    });

    it('should have UserTaskSourceKind camelCase wire values', () => {
      expect(UserTaskSourceKind.Unspecified).toBe('unspecified');
      expect(UserTaskSourceKind.Attention).toBe('attention');
      expect(UserTaskSourceKind.FailureIncident).toBe('failureIncident');
      expect(UserTaskSourceKind.Harvest).toBe('harvest');
      expect(UserTaskSourceKind.FilamentCoverage).toBe('filamentCoverage');
      expect(UserTaskSourceKind.Maintenance).toBe('maintenance');
      expect(UserTaskSourceKind.SpoolReorder).toBe('spoolReorder');
      expect(UserTaskSourceKind.PrintedPartStock).toBe('printedPartStock');
    });
  });

  describe('normalizeAnchorKind / normalizeSourceKind', () => {
    it('narrows known values through', () => {
      expect(normalizeAnchorKind('now')).toBe(UserTaskAnchorKind.Now);
      expect(normalizeAnchorKind('anytimeToday')).toBe(UserTaskAnchorKind.AnytimeToday);
      expect(normalizeSourceKind('failureIncident')).toBe(UserTaskSourceKind.FailureIncident);
    });

    it('collapses unknown / future / wrong-case values to Unspecified', () => {
      expect(normalizeAnchorKind('NOW')).toBe(UserTaskAnchorKind.Unspecified);
      expect(normalizeAnchorKind('somethingWild')).toBe(UserTaskAnchorKind.Unspecified);
      expect(normalizeAnchorKind('')).toBe(UserTaskAnchorKind.Unspecified);
      expect(normalizeAnchorKind(undefined)).toBe(UserTaskAnchorKind.Unspecified);
      expect(normalizeAnchorKind(null)).toBe(UserTaskAnchorKind.Unspecified);
      expect(normalizeAnchorKind(42)).toBe(UserTaskAnchorKind.Unspecified);
      expect(normalizeSourceKind('newSource')).toBe(UserTaskSourceKind.Unspecified);
      expect(normalizeSourceKind(undefined)).toBe(UserTaskSourceKind.Unspecified);
    });
  });

  describe('isKnownTaskType', () => {
    it('recognizes every declared TaskType', () => {
      for (const value of Object.values(TaskType)) {
        expect(isKnownTaskType(value)).toBe(true);
      }
    });

    it('rejects unknown / future strings', () => {
      expect(isKnownTaskType('SomethingNew')).toBe(false);
      expect(isKnownTaskType('')).toBe(false);
      expect(isKnownTaskType(undefined)).toBe(false);
      expect(isKnownTaskType(null)).toBe(false);
    });
  });

  describe('getShiftPlan', () => {
    it('requests /tasks?view=shift and returns a normalized shift plan', async () => {
      const raw = {
        generatedAt: '2026-07-13T12:00:00Z',
        groups: [
          {
            anchorKind: 'now',
            tasks: [
              {
                id: 't-now',
                taskType: TaskType.FailureClear,
                entityType: 'Printer',
                entityId: 'printer-1',
                title: 'Clear paused print',
                status: TaskStatus.Pending,
                priority: TaskPriority.High,
                createdAt: '2026-07-13T11:59:00Z',
                relatedEntityCount: 0,
                anchorKind: 'now',
                sourceKind: 'failureIncident',
                sourceId: 'failure:abc',
              },
            ],
          },
          {
            anchorKind: 'timeline',
            tasks: [
              {
                id: 't-at',
                taskType: TaskType.FilamentRunout,
                entityType: 'Printer',
                entityId: 'printer-2',
                title: 'Filament runout soon',
                status: TaskStatus.Pending,
                priority: TaskPriority.Normal,
                createdAt: '2026-07-13T10:00:00Z',
                relatedEntityCount: 0,
                anchorKind: 'at',
                anchorAtUtc: '2026-07-13T13:30:00Z',
                sourceKind: 'filamentCoverage',
                sourceId: 'runout:printer-2:toolhead:0',
              },
            ],
          },
        ],
      };

      vi.mocked(apiClient.get).mockResolvedValue({ data: raw });

      const result = await tasksApi.getShiftPlan();

      expect(apiClient.get).toHaveBeenCalledWith('/tasks?view=shift');
      expect(result.mode).toBe('shift');
      if (result.mode !== 'shift') return;
      expect(result.plan.groups).toHaveLength(2);
      expect(result.plan.groups[0].anchorKind).toBe(UserTaskAnchorKind.Now);
      expect(result.plan.groups[0].tasks[0].sourceKind).toBe(UserTaskSourceKind.FailureIncident);
      expect(result.plan.groups[1].anchorKind).toBe(UserTaskAnchorKind.Timeline);
      expect(result.plan.groups[1].tasks[0].anchorKind).toBe(UserTaskAnchorKind.At);
    });

    it('tolerates unknown anchor/source kinds by collapsing to Unspecified', async () => {
      vi.mocked(apiClient.get).mockResolvedValue({
        data: {
          generatedAt: '2026-07-13T12:00:00Z',
          groups: [
            {
              anchorKind: 'futureGroupKind',
              tasks: [
                {
                  id: 't1',
                  taskType: 'FutureTaskType',
                  entityType: 'Printer',
                  entityId: 'p1',
                  title: 'From the future',
                  status: TaskStatus.Pending,
                  priority: TaskPriority.Normal,
                  createdAt: '2026-07-13T11:00:00Z',
                  relatedEntityCount: 0,
                  anchorKind: 'brandNewAnchor',
                  sourceKind: 'brandNewSource',
                  sourceId: 'x',
                },
              ],
            },
          ],
        },
      });

      const result = await tasksApi.getShiftPlan();
      expect(result.mode).toBe('shift');
      if (result.mode !== 'shift') return;
      expect(result.plan.groups[0].anchorKind).toBe(UserTaskAnchorKind.Unspecified);
      const task = result.plan.groups[0].tasks[0];
      expect(task.anchorKind).toBe(UserTaskAnchorKind.Unspecified);
      expect(task.sourceKind).toBe(UserTaskSourceKind.Unspecified);
      // taskType is passed through so downstream can log/report — the widget
      // renders it as an "unrecognized task" row.
      expect(task.taskType).toBe('FutureTaskType');
    });

    it('falls back to the flat pending list on 404 (shift-plan feature disabled)', async () => {
      const flat = [
        {
          id: 'flat-1',
          taskType: TaskType.ProfileImport,
          entityType: 'PrinterModel',
          entityId: 'model-1',
          title: 'Import profiles',
          status: TaskStatus.Pending,
          priority: TaskPriority.Normal,
          createdAt: '2026-07-13T09:00:00Z',
          relatedEntityCount: 1,
        },
      ];
      vi.mocked(apiClient.get)
        .mockRejectedValueOnce({ statusCode: 404, message: 'not found', details: undefined })
        .mockResolvedValueOnce({ data: flat });

      const result = await tasksApi.getShiftPlan();

      expect(result.mode).toBe('flat');
      if (result.mode !== 'flat') return;
      expect(result.tasks).toHaveLength(1);
      expect(result.tasks[0].taskType).toBe(TaskType.ProfileImport);
      expect(vi.mocked(apiClient.get).mock.calls.map((c) => c[0])).toEqual([
        '/tasks?view=shift',
        '/tasks',
      ]);
    });

    it('propagates non-404 errors instead of masking them', async () => {
      vi.mocked(apiClient.get).mockRejectedValue({ statusCode: 500, message: 'boom' });
      await expect(tasksApi.getShiftPlan()).rejects.toMatchObject({ statusCode: 500 });
    });
  });
});
