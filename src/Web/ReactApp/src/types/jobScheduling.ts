export interface ScheduledJobDto {
  jobId: string;
  jobName: string;
  printerName: string;
  scheduledStartTime: string; // ISO string in UTC
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
  status: string; // Pending, Running, Completed, Failed, Cancelled
  message?: string;
  durationSeconds?: number;
}

export interface TimeZoneDto {
  id: string;
  displayName: string;
  offset: string;
}

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
