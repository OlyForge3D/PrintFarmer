import { apiClient } from '@/services/api';

export interface ScheduleJobRequest {
  scheduledStartTime: Date;
  timeZone?: string;
  recurrencePattern?: string;
  recurrenceEndDate?: Date;
}

export interface RescheduleJobRequest {
  newScheduledTime: Date;
  timeZone?: string;
}

export interface ScheduledJobDto {
  jobId: string;
  jobName: string;
  printerName: string;
  scheduledStartTime: string; // ISO string
  scheduledStartTimeInTimeZone: string; // ISO string in user's timezone
  timeZone: string;
  recurrencePattern?: string;
  isActive: boolean;
  isPaused: boolean;
}

export interface JobExecutionDto {
  id: string;
  scheduledExecutionTime: string; // ISO string
  actualStartTime?: string; // ISO string
  status: string;
  message?: string;
  durationSeconds?: number;
}

export interface TimeZoneDto {
  id: string;
  displayName: string;
  offset: string;
}

export const jobSchedulingService = {
  async scheduleJob(
    jobId: string,
    request: ScheduleJobRequest
  ): Promise<ScheduledJobDto> {
    return apiClient.scheduleJob(jobId, request);
  },

  async rescheduleJob(
    jobId: string,
    request: RescheduleJobRequest
  ): Promise<ScheduledJobDto> {
    return apiClient.rescheduleJob(jobId, request);
  },

  async cancelScheduling(jobId: string): Promise<void> {
    return apiClient.cancelScheduling(jobId);
  },

  async pauseScheduling(jobId: string): Promise<void> {
    return apiClient.pauseScheduling(jobId);
  },

  async resumeScheduling(jobId: string): Promise<void> {
    return apiClient.resumeScheduling(jobId);
  },

  async getScheduledJob(jobId: string): Promise<ScheduledJobDto | null> {
    return apiClient.getScheduledJob(jobId);
  },

  async getScheduledJobs(
    dateFrom?: Date,
    dateTo?: Date
  ): Promise<ScheduledJobDto[]> {
    return apiClient.getScheduledJobs(dateFrom, dateTo);
  },

  async getExecutionHistory(jobId: string): Promise<JobExecutionDto[]> {
    return apiClient.getExecutionHistory(jobId);
  },

  async getAvailableTimeZones(): Promise<TimeZoneDto[]> {
    return apiClient.getAvailableTimeZones();
  },
};
