import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { PageTemplate } from '@/common/components/PageTemplate';
import { CalendarIcon, PlusIcon, PauseIcon, PlayIcon, DeleteIcon } from '@/common/components/icons/MdiIcons';
import { Button, Card, Badge, Spinner, DataTable } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { useScheduledJobs, usePauseSchedule, useResumeSchedule, useCancelSchedule } from '@/common/hooks/useApi';
import { MonthCalendar } from '../components/MonthCalendar';
import { ScheduleModal } from '../components/ScheduleModal';
import { apiClient } from '@/services/api';
import type { JobExecution, ScheduledJob } from '@/types/api';
import {
  formatInstantInScheduleZone,
  formatScheduleWallTime,
} from '@/features/scheduling/utils/scheduleWallTime';

export function SchedulingPage() {
  const { data: scheduledJobs = [], isLoading, error } = useScheduledJobs();
  const [isScheduleModalOpen, setIsScheduleModalOpen] = useState(false);
  const [selectedDate, setSelectedDate] = useState<Date | null>(null);
  const [historySchedule, setHistorySchedule] = useState<ScheduledJob | null>(
    null
  );
  const { data: executionHistory = [], isLoading: historyLoading } = useQuery({
    queryKey: historySchedule
      ? ['scheduled-jobs', historySchedule.jobId, 'executions']
      : ['scheduled-jobs', 'history-closed'],
    queryFn: () => apiClient.getJobExecutions(historySchedule!.jobId),
    enabled: historySchedule !== null,
  });

  const pauseMutation = usePauseSchedule();
  const resumeMutation = useResumeSchedule();
  const cancelMutation = useCancelSchedule();

  const handleDateClick = (date: Date) => {
    setSelectedDate(date);
  };

  const handlePause = (jobId: string) => {
    pauseMutation.mutate(jobId);
  };

  const handleResume = (jobId: string) => {
    resumeMutation.mutate(jobId);
  };

  const handleCancel = (jobId: string) => {
    if (confirm('Are you sure you want to cancel this scheduled job?')) {
      cancelMutation.mutate(jobId);
    }
  };

  const getStatusBadgeVariant = (status: ScheduledJob['status']) => {
    switch (status) {
      case 'active':
        return 'success';
      case 'paused':
        return 'warning';
      case 'cancelled':
        return 'error';
      case 'completed':
        return 'default';
      case 'reauthorizationRequired':
        return 'error';
      default:
        return 'default';
    }
  };

  const columns = [
    {
      key: 'jobName',
      header: 'Job Name',
      sortable: true,
      render: (job: ScheduledJob) => job.jobName,
    },
    {
      key: 'printerName',
      header: 'Printer',
      sortable: true,
      render: (job: ScheduledJob) => job.printerName,
    },
    {
      key: 'scheduledStartTimeUtc',
      header: 'Scheduled Time',
      sortable: true,
      render: (job: ScheduledJob) =>
        formatScheduleWallTime(job.scheduledLocalTime, job.timeZone),
    },
    {
      key: 'recurrencePattern',
      header: 'Recurrence',
      sortable: true,
      render: (job: ScheduledJob) =>
        job.recurrencePattern
          ? `${job.recurrencePattern} × ${job.recurrenceInterval}`
          : 'Once',
    },
    {
      key: 'status',
      header: 'Status',
      sortable: true,
      render: (job: ScheduledJob) => (
        <Badge variant={getStatusBadgeVariant(job.status)}>
          {job.status}
        </Badge>
      ),
    },
    {
      key: 'actions',
      header: 'Actions',
      render: (job: ScheduledJob) => (
        <div className="flex gap-2">
          <Button
            size="sm"
            variant="subtle"
            onClick={() => setHistorySchedule(job)}
          >
            History
          </Button>
          {job.status === 'active' && (
            <Button
              size="sm"
              variant="subtle"
              onClick={() => handlePause(job.jobId)}
              loading={pauseMutation.isPending}
              iconLeft={<PauseIcon />}
            >
              Pause
            </Button>
          )}
          {job.status === 'paused' && (
            <Button
              size="sm"
              variant="subtle"
              onClick={() => handleResume(job.jobId)}
              loading={resumeMutation.isPending}
              iconLeft={<PlayIcon />}
            >
              Resume
            </Button>
          )}
          {(job.status === 'active' || job.status === 'paused') && (
            <Button
              size="sm"
              variant="danger"
              onClick={() => handleCancel(job.jobId)}
              loading={cancelMutation.isPending}
              iconLeft={<DeleteIcon />}
            >
              Cancel
            </Button>
          )}
        </div>
      ),
    },
  ];

  if (isLoading) {
    return (
      <PageTemplate title="Job Scheduling" icon={CalendarIcon}>
        <div className="flex justify-center items-center min-h-[400px]">
          <Spinner size="lg" />
        </div>
      </PageTemplate>
    );
  }

  if (error) {
    return (
      <PageTemplate title="Job Scheduling" icon={CalendarIcon}>
        <div className="text-pf-error p-4">
          Failed to load scheduled jobs: {error instanceof Error ? error.message : String(error)}
        </div>
      </PageTemplate>
    );
  }

  return (
    <PageTemplate
      title="Job Scheduling"
      subtitle="Schedule print jobs and manage recurring tasks"
      icon={CalendarIcon}
      actions={
        <Button
          variant="primary"
          iconLeft={<PlusIcon />}
          onClick={() => setIsScheduleModalOpen(true)}
        >
          Schedule Job
        </Button>
      }
    >
      <div className="space-y-6">
        {/* Calendar View */}
        <Card>
          <Card.Header>
            <h2 className="text-lg font-semibold text-pf-text-primary">
              Monthly Calendar
            </h2>
          </Card.Header>
          <Card.Body>
            <MonthCalendar
              scheduledJobs={scheduledJobs}
              onDateClick={handleDateClick}
            />
          </Card.Body>
        </Card>

        {/* Scheduled Jobs List */}
        <Card>
          <Card.Header>
            <h2 className="text-lg font-semibold text-pf-text-primary">
              Scheduled Jobs
            </h2>
          </Card.Header>
          <Card.Body>
            {scheduledJobs.length === 0 ? (
              <div className="text-center py-8 text-pf-text-secondary">
                No scheduled jobs. Click "Schedule Job" to create one.
              </div>
            ) : (
              <DataTable
                columns={columns}
                data={scheduledJobs}
                getRowKey={(job: ScheduledJob) => job.id}
                sortable
              />
            )}
          </Card.Body>
        </Card>
      </div>

      <ScheduleModal
        isOpen={isScheduleModalOpen}
        onClose={() => setIsScheduleModalOpen(false)}
        initialDate={selectedDate || undefined}
      />
      <Modal
        isOpen={historySchedule !== null}
        onClose={() => setHistorySchedule(null)}
        title={`Execution history — ${historySchedule?.jobName ?? ''}`}
        size="lg"
      >
        {historyLoading ? (
          <div className="flex justify-center py-8">
            <Spinner size="lg" />
          </div>
        ) : executionHistory.length === 0 ? (
          <p className="py-6 text-center text-pf-text-secondary">
            No execution history is available.
          </p>
        ) : (
          <ul className="space-y-3" aria-label="Scheduled execution history">
            {executionHistory.map((execution: JobExecution) => (
              <li
                key={execution.id}
                className="rounded-md border border-pf-border bg-pf-bg-1 p-3"
              >
                <div className="flex items-center justify-between gap-3">
                  <span className="font-medium text-pf-text-primary">
                    {execution.status}
                  </span>
                  <time
                    dateTime={execution.scheduledExecutionTime}
                    className="text-sm text-pf-text-secondary"
                  >
                    {formatInstantInScheduleZone(
                      execution.scheduledExecutionTime,
                      historySchedule!.timeZone
                    )}
                  </time>
                </div>
                {execution.message && (
                  <p className="mt-2 text-sm text-pf-text-secondary">
                    {execution.message}
                  </p>
                )}
              </li>
            ))}
          </ul>
        )}
      </Modal>
    </PageTemplate>
  );
}
