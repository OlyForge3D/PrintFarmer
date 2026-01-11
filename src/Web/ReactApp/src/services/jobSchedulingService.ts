import axios from 'axios';

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

const API_BASE = '/api/jobscheduling';

export const jobSchedulingService = {
  async scheduleJob(
    jobId: string,
    request: ScheduleJobRequest
  ): Promise<ScheduledJobDto> {
    const response = await axios.post<ScheduledJobDto>(
      `${API_BASE}/${jobId}/schedule`,
      {
        scheduledStartTime: request.scheduledStartTime.toISOString(),
        timeZone: request.timeZone || 'UTC',
        recurrencePattern: request.recurrencePattern || null,
        recurrenceEndDate: request.recurrenceEndDate?.toISOString() || null,
      }
    );
    return response.data;
  },

  async rescheduleJob(
    jobId: string,
    request: RescheduleJobRequest
  ): Promise<ScheduledJobDto> {
    const response = await axios.put<ScheduledJobDto>(
      `${API_BASE}/${jobId}/reschedule`,
      {
        newScheduledTime: request.newScheduledTime.toISOString(),
        timeZone: request.timeZone || 'UTC',
      }
    );
    return response.data;
  },

  async cancelScheduling(jobId: string): Promise<void> {
    await axios.delete(`${API_BASE}/${jobId}/schedule`);
  },

  async pauseScheduling(jobId: string): Promise<void> {
    await axios.post(`${API_BASE}/${jobId}/pause`);
  },

  async resumeScheduling(jobId: string): Promise<void> {
    await axios.post(`${API_BASE}/${jobId}/resume`);
  },

  async getScheduledJob(jobId: string): Promise<ScheduledJobDto | null> {
    try {
      const response = await axios.get<ScheduledJobDto>(
        `${API_BASE}/${jobId}`
      );
      return response.data;
    } catch (error: any) {
      if (error.response?.status === 404) {
        return null;
      }
      throw error;
    }
  },

  async getScheduledJobs(
    dateFrom?: Date,
    dateTo?: Date
  ): Promise<ScheduledJobDto[]> {
    const params = new URLSearchParams();
    if (dateFrom) {
      params.append('dateFrom', dateFrom.toISOString());
    }
    if (dateTo) {
      params.append('dateTo', dateTo.toISOString());
    }

    const response = await axios.get<ScheduledJobDto[]>(
      `${API_BASE}/scheduled?${params.toString()}`
    );
    return response.data;
  },

  async getExecutionHistory(jobId: string): Promise<JobExecutionDto[]> {
    const response = await axios.get<JobExecutionDto[]>(
      `${API_BASE}/${jobId}/executions`
    );
    return response.data;
  },

  async getAvailableTimeZones(): Promise<TimeZoneDto[]> {
    const response = await axios.get<TimeZoneDto[]>(
      `${API_BASE}/timezones`
    );
    return response.data;
  },
};
