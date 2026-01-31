/**
 * UpcomingMaintenanceCalendar Component
 * 
 * Displays a month-view calendar with maintenance tasks marked on their due dates.
 * Features:
 * - Month navigation
 * - Task indicators on each day
 * - Click to view day's tasks
 * - Color coding by priority/overdue status
 */

import React, { useState, useMemo } from 'react';
import { Button } from '@/common/components/ui';
import { 
  format, 
  startOfMonth, 
  endOfMonth, 
  eachDayOfInterval, 
  isSameMonth, 
  isSameDay, 
  isToday,
  addMonths,
  subMonths,
  startOfWeek,
  endOfWeek
} from 'date-fns';
import { ChevronLeftIcon, ChevronRightIcon, CalendarIcon } from '@/common/components/icons/MdiIcons';
import type { UpcomingMaintenanceTask } from '../hooks/useUpcomingMaintenance';

export interface UpcomingMaintenanceCalendarProps {
  /** Tasks to display on calendar */
  tasks: UpcomingMaintenanceTask[];
  /** Currently selected date (optional) */
  selectedDate?: Date;
  /** Callback when a day is clicked */
  onDayClick?: (date: Date, tasks: UpcomingMaintenanceTask[]) => void;
  /** Whether data is loading */
  isLoading?: boolean;
  /** Additional CSS classes */
  className?: string;
}

const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

/**
 * Get priority color for task indicator
 */
function getPriorityColor(task: UpcomingMaintenanceTask): string {
  if (task.isOverdue) return 'bg-red-500';
  switch (task.priority) {
    case 4: return 'bg-red-400';
    case 3: return 'bg-orange-400';
    case 2: return 'bg-amber-400';
    default: return 'bg-blue-400';
  }
}

interface DayCellProps {
  date: Date;
  tasks: UpcomingMaintenanceTask[];
  isCurrentMonth: boolean;
  isSelected: boolean;
  onClick: () => void;
}

function DayCell({ date, tasks, isCurrentMonth, isSelected, onClick }: DayCellProps) {
  const today = isToday(date);
  const hasOverdue = tasks.some(t => t.isOverdue);
  const hasTasks = tasks.length > 0;

  return (
    <Button
      variant={isSelected ? 'tab' : 'subtle'}
      type="button"
      onClick={onClick}
      className={`
        relative h-12 sm:h-16 p-1 text-sm border-b border-r border-pf-border rounded-none
        ${isCurrentMonth ? 'bg-pf-bg-1' : 'bg-pf-bg-2'}
        ${hasTasks ? 'hover:bg-pf-accent/10' : 'hover:bg-pf-border/30'}
      `}
      aria-label={`${format(date, 'MMMM d, yyyy')}${hasTasks ? `, ${tasks.length} maintenance task${tasks.length !== 1 ? 's' : ''}` : ''}`}
    >
      {/* Day number */}
      <span 
        className={`
          inline-flex items-center justify-center w-6 h-6 rounded-full text-xs font-medium
          ${today ? 'bg-pf-accent text-white' : ''}
          ${!today && isCurrentMonth ? 'text-pf-text-primary' : 'text-pf-text-tertiary'}
        `}
      >
        {format(date, 'd')}
      </span>

      {/* Task indicators */}
      {hasTasks && (
        <div className="absolute bottom-1 left-1 right-1 flex flex-wrap gap-0.5 justify-center">
          {tasks.slice(0, 3).map((task) => (
            <span
              key={task.id}
              className={`w-1.5 h-1.5 rounded-full ${getPriorityColor(task)}`}
              title={task.taskName}
            />
          ))}
          {tasks.length > 3 && (
            <span className="text-[10px] text-pf-text-tertiary">+{tasks.length - 3}</span>
          )}
        </div>
      )}

      {/* Overdue indicator */}
      {hasOverdue && (
        <span className="absolute top-1 right-1 w-2 h-2 bg-red-500 rounded-full animate-pulse" />
      )}
    </Button>
  );
}

/**
 * Month-view calendar showing upcoming maintenance tasks
 */
export function UpcomingMaintenanceCalendar({
  tasks,
  selectedDate,
  onDayClick,
  isLoading,
  className = '',
}: UpcomingMaintenanceCalendarProps) {
  const [currentMonth, setCurrentMonth] = useState(new Date());
  const [internalSelectedDate, setInternalSelectedDate] = useState<Date | undefined>(selectedDate);

  // Build task map by date string
  const tasksByDate = useMemo(() => {
    const map = new Map<string, UpcomingMaintenanceTask[]>();
    tasks.forEach(task => {
      const key = format(task.dueDate, 'yyyy-MM-dd');
      const existing = map.get(key) || [];
      map.set(key, [...existing, task]);
    });
    return map;
  }, [tasks]);

  // Get all days to display (including days from adjacent months to fill the grid)
  const calendarDays = useMemo(() => {
    const monthStart = startOfMonth(currentMonth);
    const monthEnd = endOfMonth(currentMonth);
    const calendarStart = startOfWeek(monthStart);
    const calendarEnd = endOfWeek(monthEnd);
    return eachDayOfInterval({ start: calendarStart, end: calendarEnd });
  }, [currentMonth]);

  const handlePrevMonth = () => setCurrentMonth(subMonths(currentMonth, 1));
  const handleNextMonth = () => setCurrentMonth(addMonths(currentMonth, 1));
  const handleToday = () => setCurrentMonth(new Date());

  const handleDayClick = (date: Date) => {
    setInternalSelectedDate(date);
    const dayTasks = tasksByDate.get(format(date, 'yyyy-MM-dd')) || [];
    onDayClick?.(date, dayTasks);
  };

  // Calculate summary stats for current month
  const monthStats = useMemo(() => {
    const monthStart = startOfMonth(currentMonth);
    const monthEnd = endOfMonth(currentMonth);
    let totalTasks = 0;
    let overdueTasks = 0;

    tasksByDate.forEach((dayTasks, dateStr) => {
      const date = new Date(dateStr);
      if (date >= monthStart && date <= monthEnd) {
        totalTasks += dayTasks.length;
        overdueTasks += dayTasks.filter(t => t.isOverdue).length;
      }
    });

    return { totalTasks, overdueTasks };
  }, [currentMonth, tasksByDate]);

  if (isLoading) {
    return (
      <div className={`bg-pf-panel border border-pf-border rounded-xl overflow-hidden ${className}`}>
        <div className="p-4 border-b border-pf-border">
          <div className="h-6 bg-pf-border rounded-sm w-40 animate-pulse" />
        </div>
        <div className="p-4">
          <div className="grid grid-cols-7 gap-0">
            {Array.from({ length: 35 }).map((_, i) => (
              <div key={i} className="h-12 sm:h-16 bg-pf-border/30 animate-pulse border-b border-r border-pf-border" />
            ))}
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className={`bg-pf-panel border border-pf-border rounded-xl overflow-hidden ${className}`}>
      {/* Header */}
      <div className="px-4 py-3 border-b border-pf-border flex items-center justify-between">
        <div className="flex items-center gap-3">
          <CalendarIcon className="h-5 w-5 text-pf-text-tertiary" aria-hidden="true" />
          <div>
            <h3 className="font-semibold text-pf-text-primary">
              {format(currentMonth, 'MMMM yyyy')}
            </h3>
            <p className="text-xs text-pf-text-tertiary">
              {monthStats.totalTasks} task{monthStats.totalTasks !== 1 ? 's' : ''} scheduled
              {monthStats.overdueTasks > 0 && (
                <span className="text-red-400 ml-1">
                  ({monthStats.overdueTasks} overdue)
                </span>
              )}
            </p>
          </div>
        </div>

        <div className="flex items-center gap-1">
          <Button
            variant="subtle"
            size="sm"
            onClick={handlePrevMonth}
            aria-label="Previous month"
          >
            <ChevronLeftIcon className="h-4 w-4" />
          </Button>
          <Button
            variant="subtle"
            size="sm"
            onClick={handleToday}
          >
            Today
          </Button>
          <Button
            variant="subtle"
            size="sm"
            onClick={handleNextMonth}
            aria-label="Next month"
          >
            <ChevronRightIcon className="h-4 w-4" />
          </Button>
        </div>
      </div>

      {/* Calendar grid */}
      <div className="overflow-x-auto">
        {/* Weekday headers */}
        <div className="grid grid-cols-7 border-b border-pf-border">
          {WEEKDAYS.map(day => (
            <div 
              key={day} 
              className="px-2 py-2 text-xs font-medium text-pf-text-tertiary text-center bg-pf-bg-2"
            >
              {day}
            </div>
          ))}
        </div>

        {/* Day cells */}
        <div className="grid grid-cols-7">
          {calendarDays.map(date => {
            const dateKey = format(date, 'yyyy-MM-dd');
            const dayTasks = tasksByDate.get(dateKey) || [];
            const isCurrentMonth = isSameMonth(date, currentMonth);
            const isSelected = internalSelectedDate ? isSameDay(date, internalSelectedDate) : false;

            return (
              <DayCell
                key={dateKey}
                date={date}
                tasks={dayTasks}
                isCurrentMonth={isCurrentMonth}
                isSelected={isSelected}
                onClick={() => handleDayClick(date)}
              />
            );
          })}
        </div>
      </div>

      {/* Legend */}
      <div className="px-4 py-2 border-t border-pf-border bg-pf-bg-2">
        <div className="flex items-center gap-4 text-xs text-pf-text-tertiary">
          <div className="flex items-center gap-1.5">
            <span className="w-2 h-2 rounded-full bg-red-500" />
            <span>Overdue</span>
          </div>
          <div className="flex items-center gap-1.5">
            <span className="w-2 h-2 rounded-full bg-red-400" />
            <span>Critical</span>
          </div>
          <div className="flex items-center gap-1.5">
            <span className="w-2 h-2 rounded-full bg-orange-400" />
            <span>High</span>
          </div>
          <div className="flex items-center gap-1.5">
            <span className="w-2 h-2 rounded-full bg-amber-400" />
            <span>Medium</span>
          </div>
          <div className="flex items-center gap-1.5">
            <span className="w-2 h-2 rounded-full bg-blue-400" />
            <span>Low</span>
          </div>
        </div>
      </div>
    </div>
  );
}
