import React, { useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { Button } from '@/common/components/ui/Button';
import { Select } from '@/common/components/ui/Select';
import { Modal } from '@/common/components/modals/Modal';
import { jobSchedulingService } from '@/services/jobSchedulingService';
import { ScheduledJobDto, TimeZoneDto } from '@/types/jobScheduling';

interface JobSchedulerProps {
  jobId: string;
  jobName: string;
  onScheduleSuccess?: () => void;
}

export const JobScheduler: React.FC<JobSchedulerProps> = ({
  jobId,
  onScheduleSuccess,
}) => {
  const [scheduledTime, setScheduledTime] = useState<string>('');
  const [selectedTimeZone, setSelectedTimeZone] = useState<string>('UTC');
  const [recurrencePattern, setRecurrencePattern] = useState<string>('');
  const [recurrenceEndDate, setRecurrenceEndDate] = useState<string>('');
  const [showForm, setShowForm] = useState(false);

  // Fetch available timezones
  const { data: timeZones = [] } = useQuery<TimeZoneDto[]>({
    queryKey: ['timezones'],
    queryFn: () => jobSchedulingService.getAvailableTimeZones(),
  });

  // Fetch current scheduling info
  const { data: scheduledJob } = useQuery<ScheduledJobDto | null>({
    queryKey: ['scheduledJob', jobId],
    queryFn: () => jobSchedulingService.getScheduledJob(jobId),
    refetchInterval: 30000, // Refresh every 30 seconds
  });

  // Schedule mutation
  const scheduleJobMutation = useMutation({
    mutationFn: () => {
      const scheduledDateTime = new Date(scheduledTime);
      return jobSchedulingService.scheduleJob(jobId, {
        scheduledStartTime: scheduledDateTime,
        timeZone: selectedTimeZone,
        recurrencePattern: recurrencePattern || undefined,
        recurrenceEndDate: recurrenceEndDate ? new Date(recurrenceEndDate) : undefined,
      });
    },
    onSuccess: () => {
      setShowForm(false);
      setScheduledTime('');
      setRecurrencePattern('');
      setRecurrenceEndDate('');
      onScheduleSuccess?.();
    },
  });

  // Cancel scheduling mutation
  const cancelMutation = useMutation({
    mutationFn: () => jobSchedulingService.cancelScheduling(jobId),
    onSuccess: onScheduleSuccess,
  });

  // Pause mutation
  const pauseMutation = useMutation({
    mutationFn: () => jobSchedulingService.pauseScheduling(jobId),
    onSuccess: onScheduleSuccess,
  });

  // Resume mutation
  const resumeMutation = useMutation({
    mutationFn: () => jobSchedulingService.resumeScheduling(jobId),
    onSuccess: onScheduleSuccess,
  });

  const formatDateTime = (date: Date) => {
    return date.toLocaleString('en-CA', {
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
    });
  };

  return (
    <div className="rounded-lg border border-pf-border bg-pf-bg-1 p-6">
      <h3 className="text-lg font-semibold text-pf-text-primary mb-4">
        Job Scheduling
      </h3>

      {scheduledJob ? (
        <div className="space-y-4">
          <div className="rounded-lg bg-pf-success-bg p-4 border border-pf-success">
            <p className="text-sm text-pf-text-primary">
              <span className="font-semibold">Scheduled for:</span>{' '}
              {formatDateTime(new Date(scheduledJob.scheduledStartTimeInTimeZone))}
            </p>
            <p className="text-sm text-pf-text-primary">
              <span className="font-semibold">Timezone:</span> {scheduledJob.timeZone}
            </p>
            {scheduledJob.recurrencePattern && (
              <p className="text-sm text-pf-text-primary">
                <span className="font-semibold">Recurrence:</span>{' '}
                {scheduledJob.recurrencePattern}
              </p>
            )}
            <p className="text-sm text-pf-text-primary">
              <span className="font-semibold">Status:</span>{' '}
              <span
                className={`inline-flex rounded-full px-2 py-1 text-xs font-semibold ${
                  scheduledJob.isPaused
                    ? 'bg-pf-warning text-pf-warning-text'
                    : 'bg-pf-success text-white'
                }`}
              >
                {scheduledJob.isPaused ? 'Paused' : 'Active'}
              </span>
            </p>
          </div>

          <div className="flex flex-wrap gap-2">
            <Button
              onClick={() => setShowForm(true)}
              variant="primary"
              size="sm"
            >
              Reschedule
            </Button>
            {scheduledJob.isPaused ? (
              <Button
                onClick={() => resumeMutation.mutate()}
                disabled={resumeMutation.isPending}
                variant="success"
                size="sm"
              >
                {resumeMutation.isPending ? 'Resuming...' : 'Resume'}
              </Button>
            ) : (
              <Button
                onClick={() => pauseMutation.mutate()}
                disabled={pauseMutation.isPending}
                variant="secondary"
                size="sm"
              >
                {pauseMutation.isPending ? 'Pausing...' : 'Pause'}
              </Button>
            )}
            <Button
              onClick={() => cancelMutation.mutate()}
              disabled={cancelMutation.isPending}
              variant="danger"
              size="sm"
            >
              {cancelMutation.isPending ? 'Canceling...' : 'Cancel Scheduling'}
            </Button>
          </div>
        </div>
      ) : (
        <div>
          <p className="text-sm text-pf-text-secondary mb-4">
            This job is not currently scheduled.
          </p>
          <Button
            onClick={() => setShowForm(true)}
            variant="primary"
            size="sm"
          >
            Schedule Job
          </Button>
        </div>
      )}

      <Modal
        isOpen={showForm}
        onClose={() => setShowForm(false)}
        title={scheduledJob ? 'Reschedule Job' : 'Schedule Job'}
        width="max-w-md"
        footer={
          <div className="flex gap-2 justify-end">
            <Button
              onClick={() => setShowForm(false)}
              variant="secondary"
              size="sm"
            >
              Cancel
            </Button>
            <Button
              onClick={() => scheduleJobMutation.mutate()}
              disabled={
                !scheduledTime ||
                scheduleJobMutation.isPending
              }
              variant="primary"
              size="sm"
            >
              {scheduleJobMutation.isPending
                ? 'Scheduling...'
                : scheduledJob
                  ? 'Reschedule'
                  : 'Schedule'}
            </Button>
          </div>
        }
      >
        <div className="space-y-4">
          {/* Date/Time Input */}
          <div>
            <label className="block text-sm font-medium text-pf-text-primary mb-1">
              Scheduled Date & Time
            </label>
            <input
              type="datetime-local"
              value={scheduledTime}
              onChange={(e) => setScheduledTime(e.target.value)}
              className="w-full rounded-sm border border-pf-border bg-pf-bg-0 text-pf-text-primary px-3 py-2 text-sm focus:border-pf-accent focus:outline-none focus:ring-1 focus:ring-pf-accent"
              required
            />
          </div>

          {/* Timezone Select */}
          <div>
            <label className="block text-sm font-medium text-pf-text-primary mb-1">
              Timezone
            </label>
            <Select
              value={selectedTimeZone}
              onChange={(e) => setSelectedTimeZone(e.target.value)}
              aria-label="Select timezone"
            >
              {timeZones.map((tz) => (
                <option key={tz.id} value={tz.id}>
                  {tz.displayName} ({tz.offset})
                </option>
              ))}
            </Select>
          </div>

          {/* Recurrence Pattern */}
          <div>
            <label className="block text-sm font-medium text-pf-text-primary mb-1">
              Recurrence (Optional)
            </label>
            <Select
              value={recurrencePattern}
              onChange={(e) => setRecurrencePattern(e.target.value)}
              aria-label="Select recurrence pattern"
            >
              <option value="">One-time</option>
              <option value="Daily">Daily</option>
              <option value="Weekly">Weekly</option>
              <option value="Monthly">Monthly</option>
            </Select>
          </div>

          {/* Recurrence End Date */}
          {recurrencePattern && (
            <div>
              <label className="block text-sm font-medium text-pf-text-primary mb-1">
                Recurrence End Date (Optional)
              </label>
              <input
                type="date"
                value={recurrenceEndDate}
                onChange={(e) => setRecurrenceEndDate(e.target.value)}
                className="w-full rounded-sm border border-pf-border bg-pf-bg-0 text-pf-text-primary px-3 py-2 text-sm focus:border-pf-accent focus:outline-none focus:ring-1 focus:ring-pf-accent"
              />
            </div>
          )}

          {/* Error Message */}
          {scheduleJobMutation.isError && (
            <div className="rounded-md bg-pf-error-bg p-3 border border-pf-error-border">
              <p className="text-sm text-pf-error-text">
                {(scheduleJobMutation.error as Error)?.message ||
                  'Failed to schedule job'}
              </p>
            </div>
          )}
        </div>
      </Modal>
    </div>
  );
};
