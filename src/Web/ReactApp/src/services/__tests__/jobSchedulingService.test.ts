import { describe, it, expect, vi, beforeEach } from 'vitest';
import { jobSchedulingService, ScheduleJobRequest, RescheduleJobRequest } from '../jobSchedulingService';
import { apiClient } from '../api';
import type { ScheduledJob } from '@/types/api';

vi.mock('../api', () => ({
  apiClient: {
    scheduleJob: vi.fn(),
    rescheduleJob: vi.fn(),
    cancelSchedule: vi.fn(),
    pauseSchedule: vi.fn(),
    resumeSchedule: vi.fn(),
    getScheduledJob: vi.fn(),
    getScheduledJobs: vi.fn(),
    getJobExecutions: vi.fn(),
    getTimezones: vi.fn(),
  },
}));

describe('jobSchedulingService', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('scheduleJob', () => {
    it('should schedule a job with all parameters', async () => {
      const jobId = 'job-123';
      const request: ScheduleJobRequest = {
        scheduledLocalTime: '2024-01-01T10:00:00',
        timeZone: 'America/New_York',
        recurrencePattern: 'Daily',
        recurrenceInterval: 1,
        recurrenceEndLocalTime: '2024-12-31T23:59:59',
      };
      const mockResponse = {
        id: 'schedule-123',
        jobId: 'job-123',
        jobName: 'Test Job',
        printerName: 'Printer 1',
        printerId: 'printer-1',
        scheduledStartTimeUtc: '2024-01-01T15:00:00Z',
        scheduledLocalTime: '2024-01-01T10:00:00',
        timeZone: 'America/New_York',
        recurrencePattern: 'Daily' as const,
        recurrenceInterval: 1,
        isActive: true,
        isPaused: false,
        requiresOperatorReauthorization: false,
        status: 'active' as const,
      };

      vi.mocked(apiClient.scheduleJob).mockResolvedValue(mockResponse);

      const result = await jobSchedulingService.scheduleJob(jobId, request);

      expect(apiClient.scheduleJob).toHaveBeenCalledWith(jobId, request);
      expect(result).toEqual(mockResponse);
    });

    it('should schedule a one-time job without recurrence', async () => {
      const jobId = 'job-456';
      const request: ScheduleJobRequest = {
        scheduledLocalTime: '2024-02-01T14:00:00',
        timeZone: 'UTC',
        recurrenceInterval: 1,
      };

      await jobSchedulingService.scheduleJob(jobId, request);

      expect(apiClient.scheduleJob).toHaveBeenCalledWith(jobId, request);
    });
  });

  describe('rescheduleJob', () => {
    it('should reschedule a job', async () => {
      const jobId = 'job-789';
      const request: RescheduleJobRequest = {
        scheduledLocalTime: '2024-03-01T12:00:00',
        timeZone: 'UTC',
        recurrenceInterval: 1,
      };
      const mockResponse = {
        id: 'schedule-789',
        jobId: 'job-789',
        jobName: 'Rescheduled Job',
        printerName: 'Printer 2',
        printerId: 'printer-2',
        scheduledStartTimeUtc: '2024-03-01T12:00:00Z',
        scheduledLocalTime: '2024-03-01T12:00:00',
        timeZone: 'UTC',
        recurrenceInterval: 1,
        isActive: true,
        isPaused: false,
        requiresOperatorReauthorization: false,
        status: 'active' as const,
      };

      vi.mocked(apiClient.rescheduleJob).mockResolvedValue(mockResponse);

      const result = await jobSchedulingService.rescheduleJob(jobId, request);

      expect(apiClient.rescheduleJob).toHaveBeenCalledWith(jobId, request);
      expect(result).toEqual(mockResponse);
    });
  });

  describe('cancelScheduling', () => {
    it('should cancel scheduling for a job', async () => {
      const jobId = 'job-cancel';

      await jobSchedulingService.cancelScheduling(jobId);

      expect(apiClient.cancelSchedule).toHaveBeenCalledWith(jobId);
    });
  });

  describe('pauseScheduling', () => {
    it('should pause scheduling for a job', async () => {
      const jobId = 'job-pause';

      await jobSchedulingService.pauseScheduling(jobId);

      expect(apiClient.pauseSchedule).toHaveBeenCalledWith(jobId);
    });
  });

  describe('resumeScheduling', () => {
    it('should resume scheduling for a job', async () => {
      const jobId = 'job-resume';

      await jobSchedulingService.resumeScheduling(jobId);

      expect(apiClient.resumeSchedule).toHaveBeenCalledWith(jobId);
    });
  });

  describe('getScheduledJob', () => {
    it('should get a scheduled job by ID', async () => {
      const jobId = 'job-get';
      const mockJob: ScheduledJob = {
        id: 'schedule-get',
        jobId: 'job-get',
        jobName: 'Get Job',
        printerName: 'Printer 3',
        printerId: 'printer-3',
        scheduledStartTimeUtc: '2024-04-01T08:00:00Z',
        scheduledLocalTime: '2024-04-01T08:00:00',
        timeZone: 'UTC',
        recurrenceInterval: 1,
        isActive: true,
        isPaused: false,
        requiresOperatorReauthorization: false,
        status: 'active',
      };

      vi.mocked(apiClient.getScheduledJob).mockResolvedValue(mockJob);

      const result = await jobSchedulingService.getScheduledJob(jobId);

      expect(apiClient.getScheduledJob).toHaveBeenCalledWith(jobId);
      expect(result).toEqual(mockJob);
    });

    it('should propagate a not-found response for a non-existent job', async () => {
      const jobId = 'non-existent';
      const notFound = new Error('Scheduled job not found');

      vi.mocked(apiClient.getScheduledJob).mockRejectedValue(notFound);

      await expect(jobSchedulingService.getScheduledJob(jobId))
        .rejects.toThrow('Scheduled job not found');
    });
  });

  describe('getScheduledJobs', () => {
    it('should get all scheduled jobs without date filters', async () => {
      const mockJobs: ScheduledJob[] = [
        {
          id: 'schedule-1',
          jobId: 'job-1',
          jobName: 'Job 1',
          printerName: 'Printer 1',
          printerId: 'printer-1',
          scheduledStartTimeUtc: '2024-05-01T10:00:00Z',
          scheduledLocalTime: '2024-05-01T10:00:00',
          timeZone: 'UTC',
          recurrenceInterval: 1,
          isActive: true,
          isPaused: false,
          requiresOperatorReauthorization: false,
          status: 'active',
        },
        {
          id: 'schedule-2',
          jobId: 'job-2',
          jobName: 'Job 2',
          printerName: 'Printer 2',
          printerId: 'printer-2',
          scheduledStartTimeUtc: '2024-05-02T14:00:00Z',
          scheduledLocalTime: '2024-05-02T14:00:00',
          timeZone: 'UTC',
          recurrenceInterval: 1,
          isActive: true,
          isPaused: true,
          requiresOperatorReauthorization: false,
          status: 'paused',
        },
      ];

      vi.mocked(apiClient.getScheduledJobs).mockResolvedValue(mockJobs);

      const result = await jobSchedulingService.getScheduledJobs();

      expect(apiClient.getScheduledJobs).toHaveBeenCalledWith(undefined, undefined);
      expect(result).toEqual(mockJobs);
    });

    it('should get scheduled jobs with date range', async () => {
      const dateFrom = new Date('2024-05-01');
      const dateTo = new Date('2024-05-31');

      await jobSchedulingService.getScheduledJobs(dateFrom, dateTo);

      expect(apiClient.getScheduledJobs).toHaveBeenCalledWith(dateFrom, dateTo);
    });
  });

  describe('getExecutionHistory', () => {
    it('should get execution history for a job', async () => {
      const jobId = 'job-history';
      const mockHistory = [
        {
          id: 'exec-1',
          scheduledExecutionTime: '2024-06-01T10:00:00Z',
          actualStartTime: '2024-06-01T10:00:05Z',
          status: 'completed',
          durationSeconds: 3600,
        },
        {
          id: 'exec-2',
          scheduledExecutionTime: '2024-06-02T10:00:00Z',
          actualStartTime: '2024-06-02T10:00:03Z',
          status: 'failed',
          message: 'Printer offline',
          durationSeconds: 0,
        },
      ];

      vi.mocked(apiClient.getJobExecutions).mockResolvedValue(mockHistory);

      const result = await jobSchedulingService.getExecutionHistory(jobId);

      expect(apiClient.getJobExecutions).toHaveBeenCalledWith(jobId);
      expect(result).toEqual(mockHistory);
    });
  });

  describe('getAvailableTimeZones', () => {
    it('should get list of available time zones', async () => {
      const mockTimeZones = [
        { id: 'UTC', displayName: 'UTC', offset: '+00:00' },
        { id: 'America/New_York', displayName: 'Eastern Time', offset: '-05:00' },
        { id: 'America/Los_Angeles', displayName: 'Pacific Time', offset: '-08:00' },
      ];

      vi.mocked(apiClient.getTimezones).mockResolvedValue(mockTimeZones);

      const result = await jobSchedulingService.getAvailableTimeZones();

      expect(apiClient.getTimezones).toHaveBeenCalled();
      expect(result).toEqual(mockTimeZones);
      expect(result).toHaveLength(3);
    });
  });
});
