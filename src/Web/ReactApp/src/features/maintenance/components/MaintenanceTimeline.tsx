/**
 * MaintenanceTimeline Component
 * 
 * Displays upcoming maintenance tasks in a timeline/list view.
 * Features:
 * - Grouped by relative date (Today, Tomorrow, This Week, etc.)
 * - Priority indicators
 * - Quick actions
 * - Scrollable with sticky headers
 */

import React, { useMemo } from 'react';
import { 
  format, 
  isToday, 
  isTomorrow, 
  isThisWeek, 
  differenceInDays,
  startOfDay
} from 'date-fns';
import { 
  ClockIcon, 
  AlertIcon, 
  CheckCircleIcon,
  ChevronRightIcon 
} from '@/common/components/icons/MdiIcons';
import { Badge, Button } from '@/common/components/ui';
import type { UpcomingMaintenanceTask } from '../hooks/useUpcomingMaintenance';

export interface MaintenanceTimelineProps {
  /** Tasks to display */
  tasks: UpcomingMaintenanceTask[];
  /** Whether data is loading */
  isLoading?: boolean;
  /** Callback when a task is clicked */
  onTaskClick?: (task: UpcomingMaintenanceTask) => void;
  /** Callback when marking task complete */
  onMarkComplete?: (task: UpcomingMaintenanceTask) => void;
  /** Maximum tasks to display before showing "show more" */
  maxVisible?: number;
  /** Additional CSS classes */
  className?: string;
}

interface TaskGroup {
  label: string;
  tasks: UpcomingMaintenanceTask[];
  isOverdue?: boolean;
}

/**
 * Get priority configuration
 */
function getPriorityConfig(priority: number): { label: string; color: string; bgColor: string } {
  switch (priority) {
    case 4: return { label: 'Critical', color: 'text-pf-error', bgColor: 'bg-pf-error/10' };
    case 3: return { label: 'High', color: 'text-pf-warning', bgColor: 'bg-pf-warning/10' };
    case 2: return { label: 'Medium', color: 'text-pf-warning', bgColor: 'bg-pf-warning/10' };
    default: return { label: 'Low', color: 'text-pf-accent', bgColor: 'bg-pf-accent-bg/15' };
  }
}

/**
 * Group tasks by relative date
 */
function groupTasksByDate(tasks: UpcomingMaintenanceTask[]): TaskGroup[] {
  const groups: TaskGroup[] = [];
  const today = startOfDay(new Date());

  const dateTasks = tasks.filter(t => Boolean(t.dueDate));
  const runtimeTasks = tasks.filter(t => t.intervalType === 'hours' && !t.isOverdue);

  // Group: Overdue
  const overdueTasks = tasks.filter(t => t.isOverdue);
  if (overdueTasks.length > 0) {
    groups.push({ label: 'Overdue', tasks: overdueTasks, isOverdue: true });
  }

  // Group: Runtime-based (hour interval)
  if (runtimeTasks.length > 0) {
    groups.push({ label: 'Runtime', tasks: runtimeTasks });
  }

  // Group: Today
  const todayTasks = dateTasks.filter(t => !t.isOverdue && isToday(t.dueDate!));
  if (todayTasks.length > 0) {
    groups.push({ label: 'Today', tasks: todayTasks });
  }

  // Group: Tomorrow
  const tomorrowTasks = dateTasks.filter(t => !t.isOverdue && isTomorrow(t.dueDate!));
  if (tomorrowTasks.length > 0) {
    groups.push({ label: 'Tomorrow', tasks: tomorrowTasks });
  }

  // Group: This Week (not today or tomorrow)
  const thisWeekTasks = dateTasks.filter(t => 
    !t.isOverdue && 
    !isToday(t.dueDate!) && 
    !isTomorrow(t.dueDate!) && 
    isThisWeek(t.dueDate!) &&
    differenceInDays(t.dueDate!, today) <= 7
  );
  if (thisWeekTasks.length > 0) {
    groups.push({ label: 'This Week', tasks: thisWeekTasks });
  }

  // Group: Next Week
  const nextWeekTasks = dateTasks.filter(t => {
    const diff = differenceInDays(t.dueDate!, today);
    return !t.isOverdue && diff > 7 && diff <= 14;
  });
  if (nextWeekTasks.length > 0) {
    groups.push({ label: 'Next Week', tasks: nextWeekTasks });
  }

  // Group: Later (more than 2 weeks)
  const laterTasks = dateTasks.filter(t => {
    const diff = differenceInDays(t.dueDate!, today);
    return !t.isOverdue && diff > 14;
  });
  if (laterTasks.length > 0) {
    groups.push({ label: 'Later', tasks: laterTasks });
  }

  return groups;
}

interface TimelineItemProps {
  task: UpcomingMaintenanceTask;
  onTaskClick?: (task: UpcomingMaintenanceTask) => void;
  onMarkComplete?: (task: UpcomingMaintenanceTask) => void;
}

function TimelineItem({ task, onTaskClick, onMarkComplete }: TimelineItemProps) {
  const priorityConfig = getPriorityConfig(task.priority);

  const runtimeDueText = (() => {
    if (task.intervalType !== 'hours') return null;
    if (task.hoursUntilDue == null) return 'Runtime-based';
    const rounded = Math.ceil(Math.abs(task.hoursUntilDue));
    if (task.isOverdue) {
      return `${rounded} hour${rounded !== 1 ? 's' : ''} overdue`;
    }
    return `Due in ${rounded} hour${rounded !== 1 ? 's' : ''}`;
  })();

  return (
    <div 
      className={`
        flex items-start gap-3 p-3 rounded-lg border transition-colors
        ${task.isOverdue 
          ? 'bg-pf-error/10 border-pf-error/30 hover:bg-pf-error/10' 
          : 'bg-pf-bg-1 border-pf-border hover:bg-pf-border/30'
        }
      `}
    >
      {/* Priority indicator */}
      <div className={`mt-1 w-2 h-2 rounded-full shrink-0 ${priorityConfig.bgColor.replace('/20', '')}`} />

      {/* Task info */}
      <div className="flex-1 min-w-0">
        <div className="flex items-start justify-between gap-2">
          <div>
            <h4 className="font-medium text-pf-text-primary text-sm truncate">
              {task.taskName}
            </h4>
            <p className="text-xs text-pf-text-secondary mt-0.5">
              {task.printerName}
              {task.component && (
                <span className="text-pf-text-tertiary"> • {task.component}</span>
              )}
            </p>
          </div>
          <Badge 
            variant="outline" 
            className={`text-xs shrink-0 ${priorityConfig.color}`}
          >
            {priorityConfig.label}
          </Badge>
        </div>

        <div className="flex items-center justify-between mt-2">
          <div className="flex items-center gap-1.5 text-xs">
            {task.intervalType === 'hours' ? (
              <>
                {(task.isOverdue ? <AlertIcon className="h-3.5 w-3.5 text-pf-error" /> : <ClockIcon className="h-3.5 w-3.5 text-pf-text-tertiary" />)}
                <span className={`${task.isOverdue ? 'text-pf-error font-medium' : 'text-pf-text-tertiary'}`}>
                  {runtimeDueText}
                </span>
              </>
            ) : task.isOverdue ? (
              <>
                <AlertIcon className="h-3.5 w-3.5 text-pf-error" />
                <span className="text-pf-error font-medium">
                  {Math.abs(task.daysUntilDue ?? 0)} day{Math.abs(task.daysUntilDue ?? 0) !== 1 ? 's' : ''} overdue
                </span>
              </>
            ) : task.isDueToday ? (
              <>
                <ClockIcon className="h-3.5 w-3.5 text-pf-warning" />
                <span className="text-pf-warning font-medium">Due today</span>
              </>
            ) : (
              <>
                <ClockIcon className="h-3.5 w-3.5 text-pf-text-tertiary" />
                <span className="text-pf-text-tertiary">
                  {task.dueDate ? `${format(task.dueDate, 'MMM d')} (${task.daysUntilDue ?? 0} day${(task.daysUntilDue ?? 0) !== 1 ? 's' : ''})` : 'Scheduled'}
                </span>
              </>
            )}
          </div>

          <div className="flex items-center gap-1">
            {onMarkComplete && (
              <Button
                variant="subtle"
                size="sm"
                onClick={(e) => {
                  e.stopPropagation();
                  onMarkComplete(task);
                }}
                className="h-7 px-2"
                title="Mark as complete"
              >
                <CheckCircleIcon className="h-4 w-4" />
              </Button>
            )}
            {onTaskClick && (
              <Button
                variant="subtle"
                size="sm"
                onClick={() => onTaskClick(task)}
                className="h-7 px-2"
                title="View details"
              >
                <ChevronRightIcon className="h-4 w-4" />
              </Button>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}

/**
 * Timeline view of upcoming maintenance tasks
 */
export function MaintenanceTimeline({
  tasks,
  isLoading,
  onTaskClick,
  onMarkComplete,
  maxVisible = 20,
  className = '',
}: MaintenanceTimelineProps) {
  const [showAll, setShowAll] = React.useState(false);

  const groups = useMemo(() => groupTasksByDate(tasks), [tasks]);

  // Calculate visible tasks count
  const totalTasks = tasks.length;

  if (isLoading) {
    return (
      <div className={`space-y-4 ${className}`}>
        {Array.from({ length: 3 }).map((_, groupIdx) => (
          <div key={groupIdx}>
            <div className="h-5 w-24 bg-pf-border rounded-sm animate-pulse mb-2" />
            <div className="space-y-2">
              {Array.from({ length: 2 }).map((_, taskIdx) => (
                <div key={taskIdx} className="h-20 bg-pf-border/50 rounded-lg animate-pulse" />
              ))}
            </div>
          </div>
        ))}
      </div>
    );
  }

  if (tasks.length === 0) {
    return (
      <div className={`text-center py-8 ${className}`}>
        <CheckCircleIcon className="h-12 w-12 text-pf-success mx-auto mb-3" />
        <h3 className="font-medium text-pf-text-primary">All caught up!</h3>
        <p className="text-sm text-pf-text-tertiary mt-1">
          No upcoming maintenance tasks scheduled
        </p>
      </div>
    );
  }

  // Filter groups based on maxVisible if not showing all
  let remainingVisible = maxVisible;
  const visibleGroups = showAll 
    ? groups 
    : groups.map(group => {
        if (remainingVisible <= 0) return { ...group, tasks: [] };
        const visibleTasks = group.tasks.slice(0, remainingVisible);
        remainingVisible -= visibleTasks.length;
        return { ...group, tasks: visibleTasks };
      }).filter(g => g.tasks.length > 0);

  return (
    <div className={`space-y-4 ${className}`}>
      {visibleGroups.map((group) => (
        <div key={group.label}>
          {/* Group header */}
          <h3 className={`
            text-xs font-semibold uppercase tracking-wider mb-2 flex items-center gap-2
            ${group.isOverdue ? 'text-pf-error' : 'text-pf-text-tertiary'}
          `}>
            {group.isOverdue && <AlertIcon className="h-3.5 w-3.5" />}
            {group.label}
            <span className="text-pf-text-tertiary font-normal">
              ({group.tasks.length})
            </span>
          </h3>

          {/* Tasks */}
          <div className="space-y-2">
            {group.tasks.map((task) => (
              <TimelineItem
                key={task.id}
                task={task}
                onTaskClick={onTaskClick}
                onMarkComplete={onMarkComplete}
              />
            ))}
          </div>
        </div>
      ))}

      {/* Show more/less button */}
      {totalTasks > maxVisible && (
        <div className="text-center pt-2">
          <Button
            variant="subtle"
            size="sm"
            onClick={() => setShowAll(!showAll)}
          >
            {showAll 
              ? 'Show less' 
              : `Show ${totalTasks - maxVisible} more task${totalTasks - maxVisible !== 1 ? 's' : ''}`
            }
          </Button>
        </div>
      )}
    </div>
  );
}
