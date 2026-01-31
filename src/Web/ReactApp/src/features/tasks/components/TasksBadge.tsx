import { useQuery } from '@tanstack/react-query';
import { tasksApi } from '@/services/tasksApi';
import { AlertCircleIcon } from '@/common/components/icons/MdiIcons';
import { Link } from 'react-router';

/**
 * TasksBadge shows a notification badge with the count of pending tasks.
 * Renders in the header/navbar area to alert users of pending actions.
 */
export function TasksBadge() {
  const { data: count = 0, isLoading } = useQuery({
    queryKey: ['tasks', 'count'],
    queryFn: () => tasksApi.getPendingCount(),
    refetchInterval: 30000, // Refresh every 30 seconds
    staleTime: 10000, // Consider data fresh for 10 seconds
  });

  // Don't show badge if no tasks or loading
  if (isLoading || count === 0) {
    return null;
  }

  return (
    <Link
      to="/dashboard"
      className="relative flex items-center p-1.5 rounded-md hover:bg-pf-bg-hover transition-colors"
      title={`${count} pending task${count !== 1 ? 's' : ''}`}
    >
      <AlertCircleIcon className="h-5 w-5 text-pf-warning" />
      <span className="absolute -top-1 -right-1 inline-flex items-center justify-center h-4 min-w-4 px-1 text-[10px] font-bold leading-none text-white bg-pf-warning rounded-full">
        {count > 9 ? '9+' : count}
      </span>
    </Link>
  );
}

export default TasksBadge;
