import { apiClient } from '@/services/api';
import type {
  JobExecution,
  RescheduleJobRequest,
  ScheduleJobRequest,
  ScheduledJob,
  TimezoneInfo,
} from '@/types/api';

export type {
  JobExecution as JobExecutionDto,
  RescheduleJobRequest,
  ScheduleJobRequest,
  ScheduledJob as ScheduledJobDto,
  TimezoneInfo as TimeZoneDto,
};

export const jobSchedulingService = {
  async scheduleJob(
    jobId: string,
    request: ScheduleJobRequest
  ): Promise<ScheduledJob> {
    return apiClient.scheduleJob(jobId, request);
  },

  async rescheduleJob(
    jobId: string,
    request: RescheduleJobRequest
  ): Promise<ScheduledJob> {
    return apiClient.rescheduleJob(jobId, request);
  },

  async cancelScheduling(jobId: string): Promise<void> {
    return apiClient.cancelSchedule(jobId);
  },

  async pauseScheduling(jobId: string): Promise<void> {
    return apiClient.pauseSchedule(jobId);
  },

  async resumeScheduling(jobId: string): Promise<void> {
    return apiClient.resumeSchedule(jobId);
  },

  async getScheduledJob(jobId: string): Promise<ScheduledJob> {
    return apiClient.getScheduledJob(jobId);
  },

  async getScheduledJobs(
    dateFrom?: Date,
    dateTo?: Date
  ): Promise<ScheduledJob[]> {
    return apiClient.getScheduledJobs(dateFrom, dateTo);
  },

  async getExecutionHistory(jobId: string): Promise<JobExecution[]> {
    return apiClient.getJobExecutions(jobId);
  },

  async getAvailableTimeZones(): Promise<TimezoneInfo[]> {
    return apiClient.getTimezones();
  },
};
