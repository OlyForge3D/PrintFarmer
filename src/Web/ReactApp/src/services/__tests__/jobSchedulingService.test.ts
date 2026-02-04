import { describe, it, expect, vi, beforeEach } from 'vitest';
import { jobSchedulingService, ScheduleJobRequest, RescheduleJobRequest } from '../jobSchedulingService';
import { apiClient } from '../api';

vi.mock('../api', () => ({
  apiClient: {
    scheduleJob: vi.fn(),
    rescheduleJob: vi.fn(),
    cancelScheduling: vi.fn(),
    pauseScheduling: vi.fn(),
    resumeScheduling: vi.fn(),
    getScheduledJob: vi.fn(),
    getScheduledJobs: vi.fn(),
    getExecutionHistory: vi.fn(),
    getAvailableTimeZones: vi.fn(),
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
        scheduledStartTime: new Date('2024-01-01T10:00:00Z'),
        timeZone: 'America/New_York',
        recurrencePattern: 'daily',
        recurrenceEndDate: new Date('2024-12-31T23:59:59Z'),
      };
      const mockResponse = {
        jobId: 'job-123',
        jobName: 'Test Job',
        printerName: 'Printer 1',
        scheduledStartTime: '2024-01-01T10:00:00Z',
        scheduledStartTimeInTimeZone: '2024-01-01T05:00:00-05:00',
        timeZone: 'America/New_York',
        recurrencePattern: 'daily',
        isActive: true,
        isPaused: false,
      };

      vi.mocked(apiClient.scheduleJob).mockResolvedValue(mockResponse);

      const result = await jobSchedulingService.scheduleJob(jobId, request);

      expect(apiClient.scheduleJob).toHaveBeenCalledWith(jobId, request);
      expect(result).toEqual(mockResponse);
    });

    it('should schedule a one-time job without recurrence', async () => {
      const jobId = 'job-456';
      const request: ScheduleJobRequest = {
        scheduledStartTime: new Date('2024-02-01T14:00:00Z'),
      };

      await jobSchedulingService.scheduleJob(jobId, request);

      expect(apiClient.scheduleJob).toHaveBeenCalledWith(jobId, request);
    });
  });

  describe('rescheduleJob', () => {
    it('should reschedule a job', async () => {
      const jobId = 'job-789';
      const request: RescheduleJobRequest = {
        newScheduledTime: new Date('2024-03-01T12:00:00Z'),
        timeZone: 'UTC',
      };
      const mockResponse = {
        jobId: 'job-789',
        jobName: 'Rescheduled Job',
        printerName: 'Printer 2',
        scheduledStartTime: '2024-03-01T12:00:00Z',
        scheduledStartTimeInTimeZone: '2024-03-01T12:00:00Z',
        timeZone: 'UTC',
        isActive: true,
        isPaused: false,
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

      expect(apiClient.cancelScheduling).toHaveBeenCalledWith(jobId);
    });
  });

  describe('pauseScheduling', () => {
    it('should pause scheduling for a job', async () => {
      const jobId = 'job-pause';

      await jobSchedulingService.pauseScheduling(jobId);

      expect(apiClient.pauseScheduling).toHaveBeenCalledWith(jobId);
    });
  });

  describe('resumeScheduling', () => {
    it('should resume scheduling for a job', async () => {
      const jobId = 'job-resume';

      await jobSchedulingService.resumeScheduling(jobId);

      expect(apiClient.resumeScheduling).toHaveBeenCalledWith(jobId);
    });
  });

  describe('getScheduledJob', () => {
    it('should get a scheduled job by ID', async () => {
      const jobId = 'job-get';
      const mockJob = {
        jobId: 'job-get',
        jobName: 'Get Job',
        printerName: 'Printer 3',
        scheduledStartTime: '2024-04-01T08:00:00Z',
        scheduledStartTimeInTimeZone: '2024-04-01T08:00:00Z',
        timeZone: 'UTC',
        isActive: true,
        isPaused: false,
      };

      vi.mocked(apiClient.getScheduledJob).mockResolvedValue(mockJob);

      const result = await jobSchedulingService.getScheduledJob(jobId);

      expect(apiClient.getScheduledJob).toHaveBeenCalledWith(jobId);
      expect(result).toEqual(mockJob);
    });

    it('should return null for non-existent job', async () => {
      const jobId = 'non-existent';

      vi.mocked(apiClient.getScheduledJob).mockResolvedValue(null);

      const result = await jobSchedulingService.getScheduledJob(jobId);

      expect(result).toBeNull();
    });
  });

  describe('getScheduledJobs', () => {
    it('should get all scheduled jobs without date filters', async () => {
      const mockJobs = [
        {
          jobId: 'job-1',
          jobName: 'Job 1',
          printerName: 'Printer 1',
          scheduledStartTime: '2024-05-01T10:00:00Z',
          scheduledStartTimeInTimeZone: '2024-05-01T10:00:00Z',
          timeZone: 'UTC',
          isActive: true,
          isPaused: false,
        },
        {
          jobId: 'job-2',
          jobName: 'Job 2',
          printerName: 'Printer 2',
          scheduledStartTime: '2024-05-02T14:00:00Z',
          scheduledStartTimeInTimeZone: '2024-05-02T14:00:00Z',
          timeZone: 'UTC',
          isActive: true,
          isPaused: true,
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

      vi.mocked(apiClient.getExecutionHistory).mockResolvedValue(mockHistory);

      const result = await jobSchedulingService.getExecutionHistory(jobId);

      expect(apiClient.getExecutionHistory).toHaveBeenCalledWith(jobId);
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

      vi.mocked(apiClient.getAvailableTimeZones).mockResolvedValue(mockTimeZones);

      const result = await jobSchedulingService.getAvailableTimeZones();

      expect(apiClient.getAvailableTimeZones).toHaveBeenCalled();
      expect(result).toEqual(mockTimeZones);
      expect(result).toHaveLength(3);
    });
  });
});
