import { describe, it, expect, vi, beforeEach } from 'vitest';
import { tasksApi, TaskStatus, TaskType, TaskPriority } from '../tasksApi';
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
      expect(result).toEqual(mockTasks);
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
      expect(result).toEqual(mockTask);
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
    });

    it('should have TaskPriority enum values', () => {
      expect(TaskPriority.Low).toBe('Low');
      expect(TaskPriority.Normal).toBe('Normal');
      expect(TaskPriority.High).toBe('High');
    });
  });
});
