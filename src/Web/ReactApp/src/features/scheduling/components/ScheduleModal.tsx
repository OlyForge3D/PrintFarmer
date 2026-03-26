import { useState, useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Modal } from '@/common/components/modals/Modal';
import { Button, Input, Select, FormField, Spinner } from '@/common/components/ui';
import { useScheduleJob, useTimezones, queryKeys } from '@/common/hooks/useApi';
import { apiClient } from '@/services/api';
import type { RecurrenceType, ScheduleJobRequest } from '@/types/api';

interface ScheduleModalProps {
  isOpen: boolean;
  onClose: () => void;
  initialDate?: Date;
  jobId?: string;
}

export function ScheduleModal({ isOpen, onClose, initialDate, jobId }: ScheduleModalProps) {
  const [selectedJobId, setSelectedJobId] = useState(jobId || '');
  const [scheduledDate, setScheduledDate] = useState('');
  const [scheduledTime, setScheduledTime] = useState('');
  const [timezone, setTimezone] = useState(Intl.DateTimeFormat().resolvedOptions().timeZone);
  const [recurrenceType, setRecurrenceType] = useState<RecurrenceType>('once');
  const [recurrenceInterval, setRecurrenceInterval] = useState(1);

  const { data: timezones = [] } = useTimezones();
  const { data: availableJobs = [], isLoading: jobsLoading } = useQuery({
    queryKey: queryKeys.jobQueue(),
    queryFn: () => apiClient.getJobQueue(),
    staleTime: 30_000,
    enabled: isOpen,
  });
  const scheduleJobMutation = useScheduleJob();

  const schedulableJobs = availableJobs.filter(
    (j) => j.job.status === 'Queued' || j.job.status === 'Assigned'
  );

  useEffect(() => {
    if (isOpen && initialDate) {
      const year = initialDate.getFullYear();
      const month = String(initialDate.getMonth() + 1).padStart(2, '0');
      const day = String(initialDate.getDate()).padStart(2, '0');
      setScheduledDate(`${year}-${month}-${day}`);
    }
  }, [isOpen, initialDate]);

  useEffect(() => {
    if (isOpen && jobId) {
      setSelectedJobId(jobId);
    }
  }, [isOpen, jobId]);

  const handleSubmit = () => {
    if (!selectedJobId || !scheduledDate || !scheduledTime) {
      return;
    }

    const scheduledDateTime = new Date(`${scheduledDate}T${scheduledTime}`);

    const request: ScheduleJobRequest = {
      scheduledTime: scheduledDateTime.toISOString(),
      timezone,
      recurrenceType,
      recurrenceInterval: recurrenceType !== 'once' ? recurrenceInterval : undefined,
    };

    scheduleJobMutation.mutate(
      { jobId: selectedJobId, request },
      {
        onSuccess: () => {
          onClose();
          resetForm();
        },
      }
    );
  };

  const resetForm = () => {
    setSelectedJobId(jobId || '');
    setScheduledDate('');
    setScheduledTime('');
    setRecurrenceType('once');
    setRecurrenceInterval(1);
  };

  const handleClose = () => {
    onClose();
    resetForm();
  };

  const isFormValid = selectedJobId && scheduledDate && scheduledTime;

  const footerButtons = (
    <>
      <Button variant="ghost" onClick={handleClose}>
        Cancel
      </Button>
      <Button
        variant="primary"
        onClick={handleSubmit}
        disabled={!isFormValid}
        loading={scheduleJobMutation.isPending}
      >
        Schedule Job
      </Button>
    </>
  );

  return (
    <Modal
      isOpen={isOpen}
      onClose={handleClose}
      title="Schedule Print Job"
      size="md"
      footer={footerButtons}
    >
      <div className="space-y-4">
        <FormField label="Print Job" htmlFor="jobId" required>
          {jobsLoading ? (
            <div className="flex items-center gap-2 py-2">
              <Spinner size="sm" />
              <span className="text-sm text-pf-text-secondary">Loading jobs…</span>
            </div>
          ) : schedulableJobs.length === 0 ? (
            <div className="rounded-md border border-pf-border bg-pf-bg-1 p-3 text-sm text-pf-text-secondary">
              No pending jobs available to schedule.
            </div>
          ) : (
            <Select
              id="jobId"
              value={selectedJobId}
              onChange={(e) => setSelectedJobId(e.target.value)}
            >
              <option value="">Select a job…</option>
              {schedulableJobs.map((j) => (
                <option key={j.job.id} value={j.job.id}>
                  {j.job.name || j.gcodeFile?.name || 'Unnamed Job'} — {j.assignedPrinter?.name || 'Unassigned'}
                </option>
              ))}
            </Select>
          )}
        </FormField>

        <FormField label="Scheduled Date" htmlFor="scheduledDate" required>
          <Input
            id="scheduledDate"
            type="date"
            value={scheduledDate}
            onChange={(e) => setScheduledDate(e.target.value)}
          />
        </FormField>

        <FormField label="Scheduled Time" htmlFor="scheduledTime" required>
          <Input
            id="scheduledTime"
            type="time"
            value={scheduledTime}
            onChange={(e) => setScheduledTime(e.target.value)}
          />
        </FormField>

        <FormField label="Timezone" htmlFor="timezone" required>
          <Select
            id="timezone"
            value={timezone}
            onChange={(e) => setTimezone(e.target.value)}
          >
            {timezones.map((tz) => (
              <option key={tz.id} value={tz.id}>
                {tz.displayName} ({tz.offset})
              </option>
            ))}
          </Select>
        </FormField>

        <FormField label="Recurrence" htmlFor="recurrenceType" required>
          <Select
            id="recurrenceType"
            value={recurrenceType}
            onChange={(e) => setRecurrenceType(e.target.value as RecurrenceType)}
          >
            <option value="once">Once</option>
            <option value="daily">Daily</option>
            <option value="weekly">Weekly</option>
            <option value="monthly">Monthly</option>
          </Select>
        </FormField>

        {recurrenceType !== 'once' && (
          <FormField
            label="Recurrence Interval"
            htmlFor="recurrenceInterval"
            helper={`Repeat every ${recurrenceInterval} ${recurrenceType === 'daily' ? 'day(s)' : recurrenceType === 'weekly' ? 'week(s)' : 'month(s)'}`}
          >
            <Input
              id="recurrenceInterval"
              type="number"
              min={1}
              value={recurrenceInterval}
              onChange={(e) => setRecurrenceInterval(parseInt(e.target.value, 10))}
            />
          </FormField>
        )}
      </div>
    </Modal>
  );
}
