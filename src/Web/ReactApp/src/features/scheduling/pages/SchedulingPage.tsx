import { useState } from 'react';
import { PageTemplate } from '@/common/components/PageTemplate';
import { CalendarIcon, PlusIcon, PauseIcon, PlayIcon, DeleteIcon } from '@/common/components/icons/MdiIcons';
import { Button, Card, Badge, Spinner, DataTable } from '@/common/components/ui';
import { useScheduledJobs, usePauseSchedule, useResumeSchedule, useCancelSchedule } from '@/common/hooks/useApi';
import { MonthCalendar } from '../components/MonthCalendar';
import { ScheduleModal } from '../components/ScheduleModal';
import type { ScheduledJob } from '@/types/api';

export function SchedulingPage() {
  const { data: scheduledJobs = [], isLoading, error } = useScheduledJobs();
  const [isScheduleModalOpen, setIsScheduleModalOpen] = useState(false);
  const [selectedDate, setSelectedDate] = useState<Date | null>(null);

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
      default:
        return 'default';
    }
  };

  const columns = [
    {
      key: 'jobName',
      label: 'Job Name',
      sortable: true,
    },
    {
      key: 'printerName',
      label: 'Printer',
      sortable: true,
    },
    {
      key: 'scheduledTime',
      label: 'Scheduled Time',
      sortable: true,
      render: (job: ScheduledJob) => new Date(job.scheduledTime).toLocaleString(),
    },
    {
      key: 'recurrence',
      label: 'Recurrence',
      sortable: true,
      render: (job: ScheduledJob) => job.recurrence || 'Once',
    },
    {
      key: 'status',
      label: 'Status',
      sortable: true,
      render: (job: ScheduledJob) => (
        <Badge variant={getStatusBadgeVariant(job.status)}>
          {job.status}
        </Badge>
      ),
    },
    {
      key: 'actions',
      label: 'Actions',
      render: (job: ScheduledJob) => (
        <div className="flex gap-2">
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
    </PageTemplate>
  );
}
