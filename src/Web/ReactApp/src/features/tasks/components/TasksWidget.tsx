import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router';
import { toast } from 'sonner';
import {
  tasksApi,
  CreateTaskDto,
  ShiftPlanResult,
  TaskPriority,
  TaskType,
  UserTask,
  UserTaskAnchorKind,
} from '@/services/tasksApi';
import {
  AlertCircleIcon,
  CheckCircleIcon,
  CheckIcon,
  ClockIcon,
  CloseIcon,
  LayersIcon,
  PackageIcon,
  PlusIcon,
  WrenchIcon,
} from '@/common/components/icons/MdiIcons';
import { Button, FormField, Input, Select, Textarea } from '@/common/components/ui';
import { DashboardWidget } from '@/common/components/DashboardWidget';
import { Modal } from '@/common/components/modals/Modal';
import {
  describeTaskSource,
  formatTaskAnchorHint,
  getAnchorGroupLabel,
  getShiftTaskDetails,
} from '../shiftPlanTaskDetails';

/**
 * Icon for a given task type. Falls back to a generic alert icon for
 * unknown / future task kinds so the row still renders safely.
 */
function TaskTypeIcon({ taskType, className }: { taskType: UserTask['taskType']; className?: string }) {
  switch (taskType) {
    case TaskType.ProfileImport:
      return <LayersIcon className={className} />;
    case TaskType.MaintenanceDue:
    case TaskType.MaintenanceInIdleWindow:
    case TaskType.CalibrationNeeded:
      return <WrenchIcon className={className} />;
    case TaskType.HarvestReady:
      return <PackageIcon className={className} />;
    case TaskType.FailureClear:
      return <AlertCircleIcon className={className} />;
    case TaskType.FilamentRunout:
      return <ClockIcon className={className} />;
    case TaskType.SpoolRestock:
    case TaskType.PrintedPartRestock:
      return <PackageIcon className={className} />;
    default:
      return <AlertCircleIcon className={className} />;
  }
}

function getPriorityClasses(priority: TaskPriority): string {
  switch (priority) {
    case TaskPriority.High:
      return 'bg-pf-error/10 text-pf-error';
    case TaskPriority.Normal:
      return 'bg-pf-warning/10 text-pf-warning';
    case TaskPriority.Low:
      return 'bg-pf-border-light text-pf-text-secondary';
    default:
      return 'bg-pf-border-light text-pf-text-secondary';
  }
}

interface TaskItemProps {
  task: UserTask;
  onNavigate: (task: UserTask) => void;
  onComplete: (taskId: string) => void;
  onSkip: (taskId: string) => void;
  onDismiss: (taskId: string) => void;
  pendingActionTaskIds: ReadonlySet<string>;
}

function TaskItem({
  task,
  onNavigate,
  onComplete,
  onSkip,
  onDismiss,
  pendingActionTaskIds,
}: TaskItemProps) {
  const details = getShiftTaskDetails(task);
  const anchorHint = formatTaskAnchorHint(task);
  const sourceLabel = describeTaskSource(task.sourceKind);
  const isActionable = !details.isUnknownKind;
  const isRowBusy = pendingActionTaskIds.has(task.id);

  const ariaLabel = `${task.title} — ${sourceLabel}${anchorHint ? `, ${anchorHint}` : ''}`;

  const handleActivate = () => {
    onNavigate(task);
  };

  const outerClasses = [
    'flex items-start gap-3 p-3 rounded-lg transition-colors group',
    isRowBusy ? 'opacity-60' : '',
  ]
    .filter(Boolean)
    .join(' ');

  const buttonClasses = [
    'flex-1 flex items-start gap-3 text-left rounded-lg transition-colors',
    isActionable ? 'hover:bg-pf-bg-2 cursor-pointer' : '',
    'disabled:cursor-default disabled:opacity-100',
  ]
    .filter(Boolean)
    .join(' ');

  return (
    <div
      className={outerClasses}
      data-testid="tasks-widget-row"
      data-task-id={task.id}
      data-task-type={typeof task.taskType === 'string' ? task.taskType : ''}
      data-anchor-kind={task.anchorKind ?? UserTaskAnchorKind.Unspecified}
      data-unknown-kind={details.isUnknownKind ? 'true' : 'false'}
    >
      {/* eslint-disable-next-line local/pf-no-raw-html-controls -- native <button> required here; wrapping in <Button> would create nested interactive controls */}
      <button
        type="button"
        className={buttonClasses}
        onClick={handleActivate}
        disabled={!isActionable}
        aria-label={ariaLabel}
        aria-busy={isRowBusy || undefined}
        data-testid="tasks-widget-row-primary"
      >
        <div className="shrink-0 mt-0.5">
          <div className="p-2 rounded-lg bg-pf-warning/10">
            <TaskTypeIcon taskType={task.taskType} className="h-5 w-5 text-pf-warning" />
          </div>
        </div>
        <div className="flex-1 min-w-0">
          <h4 className="text-sm font-medium text-pf-text-primary truncate">{task.title}</h4>
          {task.description && (
            <p className="text-xs text-pf-text-secondary mt-0.5 line-clamp-2">{task.description}</p>
          )}
          <div className="flex items-center gap-2 mt-1.5 flex-wrap">
            <span className={`text-xs px-1.5 py-0.5 rounded-sm ${getPriorityClasses(task.priority)}`}>
              {task.priority}
            </span>
            <span className="text-xs text-pf-text-tertiary">{details.categoryLabel}</span>
            {anchorHint && (
              <span className="text-xs text-pf-text-tertiary">{anchorHint}</span>
            )}
            {task.relatedEntityCount > 0 && (
              <span className="text-xs text-pf-text-tertiary">
                {task.relatedEntityCount} printer{task.relatedEntityCount !== 1 ? 's' : ''} waiting
              </span>
            )}
            {details.isUnknownKind && (
              <span className="text-xs text-pf-text-tertiary italic" data-testid="tasks-widget-unknown-badge">
                Unrecognized task — server info only
              </span>
            )}
          </div>
        </div>
      </button>

      <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100 focus-within:opacity-100 transition-opacity shrink-0">
        <Button
          variant="subtle"
          size="sm"
          onClick={(e) => {
            e.stopPropagation();
            onComplete(task.id);
          }}
          title="Mark complete"
          aria-label={`Complete ${task.title}`}
          className="h-7 w-7 p-0"
          disabled={isRowBusy}
          data-testid="tasks-widget-complete"
        >
          <CheckIcon className="h-4 w-4 text-pf-text-tertiary hover:text-pf-status-online-text" />
        </Button>
        <Button
          variant="subtle"
          size="sm"
          onClick={(e) => {
            e.stopPropagation();
            onSkip(task.id);
          }}
          title="Skip task"
          aria-label={`Skip ${task.title}`}
          className="h-7 w-7 p-0"
          disabled={isRowBusy}
          data-testid="tasks-widget-skip"
        >
          <CloseIcon className="h-4 w-4 text-pf-text-tertiary hover:text-pf-text-secondary" />
        </Button>
        <Button
          variant="subtle"
          size="sm"
          onClick={(e) => {
            e.stopPropagation();
            onDismiss(task.id);
          }}
          title="Dismiss task"
          aria-label={`Dismiss ${task.title}`}
          className="h-7 w-7 p-0"
          disabled={isRowBusy}
          data-testid="tasks-widget-dismiss"
        >
          <AlertCircleIcon className="h-4 w-4 text-pf-text-tertiary hover:text-pf-text-secondary" />
        </Button>
      </div>
    </div>
  );
}

interface TaskListGroup {
  key: string;
  anchorKind: UserTaskAnchorKind;
  label: string;
  tasks: UserTask[];
}

/**
 * Build the visual group list. In shift-plan mode the server has already
 * grouped and ordered tasks deterministically; we preserve that order and
 * only render a header for each group. In flat mode we render a single
 * unlabeled group so legacy backends (or the 404 fallback) still work.
 */
function buildGroups(result: ShiftPlanResult): TaskListGroup[] {
  if (result.mode === 'flat') {
    return result.tasks.length === 0
      ? []
      : [
          {
            key: 'flat',
            anchorKind: UserTaskAnchorKind.Unspecified,
            label: '',
            tasks: result.tasks,
          },
        ];
  }
  return result.plan.groups
    .filter((g) => g.tasks.length > 0)
    .map((g, idx) => ({
      key: `${g.anchorKind}-${idx}`,
      anchorKind: g.anchorKind,
      label: getAnchorGroupLabel(g.anchorKind),
      tasks: g.tasks,
    }));
}

function countTasks(result: ShiftPlanResult | undefined): number {
  if (!result) return 0;
  if (result.mode === 'flat') return result.tasks.length;
  return result.plan.groups.reduce((sum, g) => sum + g.tasks.length, 0);
}

/**
 * TasksWidget displays pending user tasks on the dashboard.
 *
 * When the shift-plan feature is enabled server-side (issue #713), the widget
 * consumes `GET /api/tasks?view=shift` and renders the server-provided anchor
 * groups (Now → Timeline → AnytimeToday). When the feature is disabled (404),
 * it transparently falls back to the flat `GET /api/tasks` list so existing
 * profile-import and manual tasks stay visible.
 */
export function TasksWidget() {
  const queryClient = useQueryClient();
  const navigate = useNavigate();

  const [isCreateModalOpen, setIsCreateModalOpen] = useState(false);
  const [newTaskTitle, setNewTaskTitle] = useState('');
  const [newTaskDescription, setNewTaskDescription] = useState('');
  const [newTaskPriority, setNewTaskPriority] = useState<TaskPriority>(TaskPriority.Normal);
  const [pendingActionTaskIds, setPendingActionTaskIds] = useState<Set<string>>(new Set());
  const addPending = (id: string) =>
    setPendingActionTaskIds((prev) => { const s = new Set(prev); s.add(id); return s; });
  const removePending = (id: string) =>
    setPendingActionTaskIds((prev) => { const s = new Set(prev); s.delete(id); return s; });

  const { data, isLoading, error } = useQuery({
    queryKey: ['tasks', 'shift'],
    queryFn: () => tasksApi.getShiftPlan(),
    staleTime: 30_000,
    refetchInterval: 30_000,
  });

  const invalidateTasks = () => queryClient.invalidateQueries({ queryKey: ['tasks'] });

  const withPendingAction =
    <TArgs extends [string]>(
      fn: (...args: TArgs) => Promise<unknown>,
      { successMessage, errorMessage }: { successMessage: string; errorMessage: string },
    ) =>
      async (...args: TArgs) => {
        addPending(args[0]);
        try {
          await fn(...args);
          toast.success(successMessage);
          invalidateTasks();
        } catch {
          toast.error(errorMessage);
        } finally {
          removePending(args[0]);
        }
      };

  const completeMutation = useMutation({
    mutationFn: withPendingAction((taskId: string) => tasksApi.completeTask(taskId), {
      successMessage: 'Task marked complete',
      errorMessage: 'Failed to complete task',
    }),
  });

  const skipMutation = useMutation({
    mutationFn: withPendingAction((taskId: string) => tasksApi.skipTask(taskId), {
      successMessage: 'Task skipped',
      errorMessage: 'Failed to skip task',
    }),
  });

  const dismissMutation = useMutation({
    mutationFn: withPendingAction((taskId: string) => tasksApi.dismissTask(taskId), {
      successMessage: 'Task dismissed',
      errorMessage: 'Failed to dismiss task',
    }),
  });

  const createMutation = useMutation({
    mutationFn: (dto: CreateTaskDto) => tasksApi.createTask(dto),
    onSuccess: () => {
      invalidateTasks();
      toast.success('Task created');
      setNewTaskTitle('');
      setNewTaskDescription('');
      setNewTaskPriority(TaskPriority.Normal);
      setIsCreateModalOpen(false);
    },
    onError: () => {
      toast.error('Failed to create task');
    },
  });

  const handleCreateTask = () => {
    if (!newTaskTitle.trim()) {
      toast.error('Title is required');
      return;
    }
    createMutation.mutate({
      title: newTaskTitle.trim(),
      description: newTaskDescription.trim() || undefined,
      priority: newTaskPriority,
    });
  };

  /**
   * Navigate to a task's deep-link. Unknown / manual tasks fall back to a
   * toast so operators still see the title.
   */
  const handleNavigate = (task: UserTask) => {
    const details = getShiftTaskDetails(task);
    if (details.href) {
      navigate(details.href);
      return;
    }
    toast.info(task.title);
  };

  const groups = data ? buildGroups(data) : [];
  const taskCount = countTasks(data);

  const badge =
    taskCount > 0 ? (
      <span
        className="inline-flex items-center justify-center h-5 min-w-5 px-1.5 text-xs font-medium rounded-full bg-pf-warning text-[var(--pf-text-inverse)]"
        data-pf-radius="full"
        data-testid="tasks-widget-badge"
      >
        {taskCount}
      </span>
    ) : undefined;

  const headerAction = (
    <Button
      variant="subtle"
      size="sm"
      onClick={() => setIsCreateModalOpen(true)}
      title="Create new task"
      iconLeft={<PlusIcon className="h-4 w-4" />}
    >
      New
    </Button>
  );

  const emptyState = (
    <div className="text-center py-6" data-testid="tasks-widget-empty">
      <CheckCircleIcon className="h-8 w-8 text-pf-status-online-text mx-auto mb-2" />
      <p className="text-sm text-pf-text-secondary">All caught up!</p>
    </div>
  );

  return (
    <>
      <DashboardWidget
        title="Pending Tasks"
        icon={AlertCircleIcon}
        iconColorClass="text-pf-warning"
        iconBgClass="bg-pf-warning/10"
        badge={badge}
        headerAction={headerAction}
        collapsible
        storageKey="tasks-widget"
        hasContent={taskCount > 0}
        emptyState={emptyState}
        isLoading={isLoading}
        error={error ? 'Failed to load tasks' : undefined}
      >
        <div
          className="max-h-96 overflow-y-auto -m-3"
          data-testid="tasks-widget-list"
          data-mode={data?.mode ?? 'unknown'}
        >
          {groups.map((group) => (
            <section
              key={group.key}
              aria-label={group.label || undefined}
              data-testid="tasks-widget-group"
              data-anchor-kind={group.anchorKind}
            >
              {group.label && (
                <header className="px-3 pt-3 pb-1 text-xs font-semibold uppercase tracking-wide text-pf-text-tertiary">
                  {group.label}
                </header>
              )}
              <div className="divide-y divide-pf-border">
                {group.tasks.map((task) => (
                  <TaskItem
                    key={task.id}
                    task={task}
                    onNavigate={handleNavigate}
                    onComplete={(id) => completeMutation.mutate(id)}
                    onSkip={(id) => skipMutation.mutate(id)}
                    onDismiss={(id) => dismissMutation.mutate(id)}
                    pendingActionTaskIds={pendingActionTaskIds}
                  />
                ))}
              </div>
            </section>
          ))}
        </div>
      </DashboardWidget>

      <Modal
        isOpen={isCreateModalOpen}
        onClose={() => setIsCreateModalOpen(false)}
        title="Create Task"
        size="md"
        footer={
          <>
            <Button onClick={() => setIsCreateModalOpen(false)}>Cancel</Button>
            <Button
              variant="primary"
              loading={createMutation.isPending}
              disabled={createMutation.isPending}
              onClick={handleCreateTask}
            >
              Create
            </Button>
          </>
        }
      >
        <div className="space-y-4">
          <FormField label="Title" htmlFor="task-title" required>
            <Input
              id="task-title"
              value={newTaskTitle}
              onChange={(e) => setNewTaskTitle(e.target.value)}
              placeholder="Enter task title"
            />
          </FormField>

          <FormField label="Description" htmlFor="task-description">
            <Textarea
              id="task-description"
              value={newTaskDescription}
              onChange={(e) => setNewTaskDescription(e.target.value)}
              placeholder="Enter task description (optional)"
              rows={3}
            />
          </FormField>

          <FormField label="Priority" htmlFor="task-priority">
            <Select
              id="task-priority"
              value={newTaskPriority}
              onChange={(e) => setNewTaskPriority(e.target.value as TaskPriority)}
            >
              <option value={TaskPriority.Low}>Low</option>
              <option value={TaskPriority.Normal}>Normal</option>
              <option value={TaskPriority.High}>High</option>
            </Select>
          </FormField>
        </div>
      </Modal>
    </>
  );
}

export default TasksWidget;
