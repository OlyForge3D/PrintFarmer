import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { tasksApi, UserTask, TaskType, TaskPriority } from '@/services/tasksApi';
import { AlertCircleIcon, CheckCircleIcon, CloseIcon, LayersIcon, WrenchIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { useNavigate } from 'react-router-dom';
import { toast } from 'sonner';

/**
 * Get icon for task type
 */
function getTaskIcon(taskType: TaskType): React.ComponentType<{ className?: string }> {
  switch (taskType) {
    case TaskType.ProfileImport:
      return LayersIcon;
    case TaskType.MaintenanceDue:
      return WrenchIcon;
    default:
      return AlertCircleIcon;
  }
}

/**
 * Get color classes for task priority
 */
function getPriorityClasses(priority: TaskPriority): string {
  switch (priority) {
    case TaskPriority.High:
      return 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400';
    case TaskPriority.Normal:
      return 'bg-yellow-100 text-yellow-800 dark:bg-yellow-900/30 dark:text-yellow-400';
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
  const Icon = getTaskIcon(task.taskType);
  
  return (
    <div 
      className="flex items-start gap-3 p-3 hover:bg-pf-bg-hover rounded-lg transition-colors group cursor-pointer"
      onClick={() => onNavigate(task)}
      role="button"
      tabIndex={0}
      onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onNavigate(task); } }}
    >
      <div className="flex-shrink-0 mt-0.5">
        <div className="p-2 rounded-lg bg-pf-warning/10">
          <Icon className="h-5 w-5 text-pf-warning" />
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
              <span className={`text-xs px-1.5 py-0.5 rounded ${getPriorityClasses(task.priority)}`}>
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
  
  // Fetch pending tasks
  const { data: tasks, isLoading, error } = useQuery({
    queryKey: ['tasks', 'pending'],
    queryFn: () => tasksApi.getPendingTasks(),
    refetchInterval: 30000, // Refresh every 30 seconds
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

  // Don't render if no tasks
  if (!isLoading && (!tasks || tasks.length === 0)) {
    return null;
  }

  return (
    <div className="bg-pf-bg-1 border border-pf-border rounded-xl shadow-lg overflow-hidden">
      {/* Header */}
      <div className="px-4 py-3 border-b border-pf-border flex items-center justify-between">
        <div className="flex items-center gap-2">
          <AlertCircleIcon className="h-5 w-5 text-pf-warning" />
          <h3 className="text-sm font-semibold text-pf-text-primary">
            Pending Tasks
          </h3>
          {tasks && tasks.length > 0 && (
            <span className="inline-flex items-center justify-center h-5 min-w-[1.25rem] px-1.5 text-xs font-medium rounded-full bg-pf-warning text-white">
              {tasks.length}
            </span>
          )}
        </div>
      </div>

      {/* Content */}
      <div className="divide-y divide-pf-border">
        {isLoading ? (
          <div className="p-4">
            <div className="animate-pulse space-y-3">
              <div className="h-12 bg-pf-loading rounded" />
              <div className="h-12 bg-pf-loading rounded" />
            </div>
          </div>
        ) : error ? (
          <div className="p-4 text-center text-pf-text-secondary text-sm">
            Failed to load tasks
          </div>
        ) : tasks && tasks.length > 0 ? (
          <div className="max-h-80 overflow-y-auto">
            {tasks.map((task) => (
              <TaskItem
                key={task.id}
                task={task}
                onSkip={(id) => skipMutation.mutate(id)}
                onNavigate={handleNavigate}
              />
            ))}
          </div>
        ) : (
          <div className="p-6 text-center">
            <CheckCircleIcon className="h-8 w-8 text-pf-status-online-text mx-auto mb-2" />
            <p className="text-sm text-pf-text-secondary">All caught up!</p>
          </div>
        )}
      </div>
    </div>
  );
}

export default TasksWidget;
