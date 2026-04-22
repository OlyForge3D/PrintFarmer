import { useMemo } from 'react';
import { Badge } from '@/common/components/ui';

interface EstimatedCompletionBadgeProps {
  completionTimeUtc: string;
  className?: string;
}

function formatTimeRemaining(completionTimeUtc: string): string {
  const now = Date.now();
  const completion = new Date(completionTimeUtc).getTime();
  const diffMs = completion - now;

  if (diffMs <= 0) return 'Done soon';

  const totalMinutes = Math.round(diffMs / 60_000);
  if (totalMinutes < 60) return `~${totalMinutes}m left`;

  const hours = Math.floor(totalMinutes / 60);
  const minutes = totalMinutes % 60;
  if (hours < 24) {
    return minutes > 0 ? `~${hours}h ${minutes}m left` : `~${hours}h left`;
  }

  const days = Math.floor(hours / 24);
  const remainingHours = hours % 24;
  return remainingHours > 0 ? `~${days}d ${remainingHours}h left` : `~${days}d left`;
}

function formatCompletionTime(completionTimeUtc: string): string {
  const date = new Date(completionTimeUtc);
  return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

export function EstimatedCompletionBadge({ completionTimeUtc, className }: EstimatedCompletionBadgeProps) {
  const label = useMemo(() => formatTimeRemaining(completionTimeUtc), [completionTimeUtc]);
  const finishTime = useMemo(() => formatCompletionTime(completionTimeUtc), [completionTimeUtc]);

  return (
    <div className={`flex items-center gap-2 text-xs text-pf-text-secondary mb-2 ${className ?? ''}`}>
      <Badge variant="default" size="sm">⏱ {label}</Badge>
      <span className="opacity-70">done ~{finishTime}</span>
    </div>
  );
}
