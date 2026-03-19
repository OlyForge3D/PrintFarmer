import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { tasksApi, UserTask, TaskType, TaskPriority, CreateTaskDto } from '@/services/tasksApi';
import { AlertCircleIcon, CheckCircleIcon, CloseIcon, LayersIcon, WrenchIcon, PlusIcon } from '@/common/components/icons/MdiIcons';
import { Button, FormField, Input, Textarea, Select } from '@/common/components/ui';
import { DashboardWidget } from '@/common/components/DashboardWidget';
import { Modal } from '@/common/components/modals/Modal';
import { useNavigate } from 'react-router';
import { toast } from 'sonner';
import { useState } from 'react';

/**
 * Render icon for task type inline to avoid dynamic component creation
 */
function TaskTypeIcon({ taskType, className }: { taskType: TaskType; className?: string }) {
  switch (taskType) {
    case TaskType.ProfileImport:
      return <LayersIcon className={className} />;
    case TaskType.MaintenanceDue:
      return <WrenchIcon className={className} />;
    default:
      return <AlertCircleIcon className={className} />;
  }
}

/**
 * Get color classes for task priority
 */
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
  onSkip: (taskId: string) => void;
  onNavigate: (task: UserTask) => void;
}

function TaskItem({ task, onSkip, onNavigate }: TaskItemProps) {
  return (
    <div 
      className="flex items-start gap-3 p-3 hover:bg-pf-bg-hover rounded-lg transition-colors group cursor-pointer"
      onClick={() => onNavigate(task)}
      role="button"
      tabIndex={0}
      onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onNavigate(task); } }}
    >
      <div className="shrink-0 mt-0.5">
        <div className="p-2 rounded-lg bg-pf-warning/10">
          <TaskTypeIcon taskType={task.taskType} className="h-5 w-5 text-pf-warning" />
        </div>
      </div>
      
      <div className="flex-1 min-w-0">
        <div className="flex items-start justify-between gap-2">
          <div>
            <h4 className="text-sm font-medium text-pf-text-primary truncate">
              {task.title}
            </h4>
            {task.description && (
              <p className="text-xs text-pf-text-secondary mt-0.5 line-clamp-2">
                {task.description}
              </p>
            )}
            <div className="flex items-center gap-2 mt-1.5">
              <span className={`text-xs px-1.5 py-0.5 rounded-sm ${getPriorityClasses(task.priority)}`}>
                {task.priority}
              </span>
              {task.relatedEntityCount > 0 && (
                <span className="text-xs text-pf-text-tertiary">
                  {task.relatedEntityCount} printer{task.relatedEntityCount !== 1 ? 's' : ''} waiting
                </span>
              )}
            </div>
          </div>
          
          {/* Skip button */}
          <div className="flex items-center opacity-0 group-hover:opacity-100 transition-opacity">
            <Button
              variant="subtle"
              size="sm"
              onClick={(e) => { e.stopPropagation(); onSkip(task.id); }}
              title="Skip task"
              className="h-7 w-7 p-0"
            >
              <CloseIcon className="h-4 w-4 text-pf-text-tertiary hover:text-pf-text-secondary" />
            </Button>
          </div>
        </div>
      </div>
    </div>
  );
}

/**
 * TasksWidget displays pending user tasks on the dashboard.
 * Provides quick access to profile imports, maintenance, and other actionable items.
 */
export function TasksWidget() {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const [isCreateModalOpen, setIsCreateModalOpen] = useState(false);
  const [newTaskTitle, setNewTaskTitle] = useState('');
  const [newTaskDescription, setNewTaskDescription] = useState('');
  const [newTaskPriority, setNewTaskPriority] = useState<TaskPriority>(TaskPriority.Normal);
  
  // Fetch pending tasks
  const { data: tasks, isLoading, error } = useQuery({
    queryKey: ['tasks', 'pending'],
    queryFn: () => tasksApi.getPendingTasks(),
    staleTime: 30_000,
    refetchInterval: 30000,
  });

  // Skip task mutation
  const skipMutation = useMutation({
    mutationFn: (taskId: string) => tasksApi.skipTask(taskId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['tasks'] });
      toast.success('Task skipped');
    },
    onError: () => {
      toast.error('Failed to skip task');
    },
  });

  // Create task mutation
  const createMutation = useMutation({
    mutationFn: (dto: CreateTaskDto) => tasksApi.createTask(dto),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['tasks'] });
      toast.success('Task created');
      // Reset form
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

  // Navigate to task action
  const handleNavigate = (task: UserTask) => {
    switch (task.taskType) {
      case TaskType.ProfileImport:
        // Navigate to profile import wizard with model context
        // The task's metadataJson contains printerModelId for filtering profiles
      {
        let modelId = task.entityId;
        if (task.metadataJson) {
          try {
            const metadata = JSON.parse(task.metadataJson);
            if (metadata.printerModelId) {
              modelId = metadata.printerModelId;
            }
          } catch {
            // Use entityId as fallback if JSON parsing fails
          }
        }
        navigate(`/profiles/import?modelId=${modelId}&taskId=${task.id}`);
        break;
      }
      case TaskType.MaintenanceDue:
        navigate(`/maintenance?printerId=${task.entityId}`);
        break;
      default:
        // For unknown types, show task details
        toast.info(`Task: ${task.title}`);
    }
  };

  const taskCount = tasks?.length ?? 0;
  const badge = taskCount > 0 ? (
    <span className="inline-flex items-center justify-center h-5 min-w-5 px-1.5 text-xs font-medium rounded-full bg-pf-warning text-white">
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
    <div className="text-center py-6">
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
        <div className="max-h-80 overflow-y-auto divide-y divide-pf-border -m-3">
          {tasks?.map((task) => (
            <TaskItem
              key={task.id}
              task={task}
              onSkip={(id) => skipMutation.mutate(id)}
              onNavigate={handleNavigate}
            />
          ))}
        </div>
      </DashboardWidget>

      {/* Create Task Modal */}
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
