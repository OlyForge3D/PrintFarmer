import { useMemo, useState } from 'react';
import clsx from 'clsx';
import { Badge, Button } from '@/common/components/ui';
import type { ScheduledJob } from '@/types/api';

interface MonthCalendarProps {
  scheduledJobs: ScheduledJob[];
  onDateClick: (date: Date) => void;
}

export function MonthCalendar({ scheduledJobs, onDateClick }: MonthCalendarProps) {
  const [currentMonth, setCurrentMonth] = useState(new Date());

  const { daysInMonth, firstDayOfMonth, monthName } = useMemo(() => {
    const year = currentMonth.getFullYear();
    const month = currentMonth.getMonth();
    const firstDay = new Date(year, month, 1);
    const lastDay = new Date(year, month + 1, 0);
    
    return {
      daysInMonth: lastDay.getDate(),
      firstDayOfMonth: firstDay.getDay(),
      monthName: currentMonth.toLocaleString('default', { month: 'long', year: 'numeric' }),
    };
  }, [currentMonth]);

  const jobsByDate = useMemo(() => {
    const map = new Map<string, ScheduledJob[]>();
    scheduledJobs.forEach((job) => {
      const date = new Date(job.scheduledTime);
      const dateKey = `${date.getFullYear()}-${date.getMonth()}-${date.getDate()}`;
      if (!map.has(dateKey)) {
        map.set(dateKey, []);
      }
      map.get(dateKey)?.push(job);
    });
    return map;
  }, [scheduledJobs]);

  const goToPreviousMonth = () => {
    setCurrentMonth(new Date(currentMonth.getFullYear(), currentMonth.getMonth() - 1, 1));
  };

  const goToNextMonth = () => {
    setCurrentMonth(new Date(currentMonth.getFullYear(), currentMonth.getMonth() + 1, 1));
  };

  const goToToday = () => {
    setCurrentMonth(new Date());
  };

  const getDayKey = (day: number) => {
    return `${currentMonth.getFullYear()}-${currentMonth.getMonth()}-${day}`;
  };

  const isToday = (day: number) => {
    const today = new Date();
    return (
      day === today.getDate() &&
      currentMonth.getMonth() === today.getMonth() &&
      currentMonth.getFullYear() === today.getFullYear()
    );
  };

  return (
    <div className="space-y-4">
      {/* Calendar Header */}
      <div className="flex items-center justify-between">
        <h3 className="text-lg font-semibold text-pf-text-primary">{monthName}</h3>
        <div className="flex gap-2">
          <Button
            variant="unstyled"
            size="sm"
            onClick={goToPreviousMonth}
            className="px-3 py-1.5 text-sm rounded-md bg-pf-surface hover:bg-pf-hover text-pf-text-primary transition-colors"
            aria-label="Previous month"
          >
            ←
          </Button>
          <Button
            variant="unstyled"
            size="sm"
            onClick={goToToday}
            className="px-3 py-1.5 text-sm rounded-md bg-pf-surface hover:bg-pf-hover text-pf-text-primary transition-colors"
          >
            Today
          </Button>
          <Button
            variant="unstyled"
            size="sm"
            onClick={goToNextMonth}
            className="px-3 py-1.5 text-sm rounded-md bg-pf-surface hover:bg-pf-hover text-pf-text-primary transition-colors"
            aria-label="Next month"
          >
            →
          </Button>
        </div>
      </div>

      {/* Day of Week Headers */}
      <div className="grid grid-cols-7 gap-2">
        {['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'].map((day) => (
          <div
            key={day}
            className="text-center text-sm font-medium text-pf-text-secondary py-2"
          >
            {day}
          </div>
        ))}
      </div>

      {/* Calendar Grid */}
      <div className="grid grid-cols-7 gap-2">
        {/* Empty cells for days before first of month */}
        {Array.from({ length: firstDayOfMonth }).map((_, i) => (
          <div key={`empty-${i}`} className="aspect-square" />
        ))}

        {/* Days of month */}
        {Array.from({ length: daysInMonth }).map((_, i) => {
          const day = i + 1;
          const dateKey = getDayKey(day);
          const jobsOnDate = jobsByDate.get(dateKey) || [];
          const hasJobs = jobsOnDate.length > 0;
          const today = isToday(day);

          return (
            <Button
              key={day}
              variant="unstyled"
              onClick={() => {
                const clickedDate = new Date(
                  currentMonth.getFullYear(),
                  currentMonth.getMonth(),
                  day
                );
                onDateClick(clickedDate);
              }}
              className={clsx(
                'aspect-square p-2 rounded-lg border transition-colors',
                'flex flex-col items-start justify-start',
                'hover:bg-pf-hover',
                today
                  ? 'border-pf-accent bg-pf-accent/10'
                  : 'border-pf-border bg-pf-surface'
              )}
            >
              <span
                className={clsx(
                  'text-sm font-medium mb-1',
                  today ? 'text-pf-accent' : 'text-pf-text-primary'
                )}
              >
                {day}
              </span>
              {hasJobs && (
                <div className="flex flex-col gap-1 w-full">
                  {jobsOnDate.slice(0, 2).map((job) => (
                    <Badge
                      key={job.id}
                      variant={
                        job.status === 'active'
                          ? 'success'
                          : job.status === 'paused'
                            ? 'warning'
                            : 'default'
                      }
                      size="sm"
                      className="truncate text-xs"
                    >
                      {job.jobName}
                    </Badge>
                  ))}
                  {jobsOnDate.length > 2 && (
                    <span className="text-xs text-pf-text-tertiary">
                      +{jobsOnDate.length - 2} more
                    </span>
                  )}
                </div>
              )}
            </Button>
          );
        })}
      </div>
    </div>
  );
}
