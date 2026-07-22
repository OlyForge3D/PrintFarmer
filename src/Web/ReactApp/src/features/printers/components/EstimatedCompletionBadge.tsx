import { useState, useEffect } from 'react';
import { Badge } from '@/common/components/ui';
import { formatTimeRemaining, formatCompletionTime } from '@/features/printers/utils/completionTime';

interface EstimatedCompletionBadgeProps {
  completionTimeUtc?: string;
  printTimeLeftSeconds?: number;
  className?: string;
}

export function EstimatedCompletionBadge({ completionTimeUtc, printTimeLeftSeconds, className }: EstimatedCompletionBadgeProps) {
  // Tick every 30s so the countdown label stays fresh
  const [now, setNow] = useState(Date.now);
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), 30_000);
    return () => clearInterval(id);
  }, []);

  // Prefer printTimeLeftSeconds (live from SignalR) over static API timestamp
  const completionMs = printTimeLeftSeconds != null
    ? now + printTimeLeftSeconds * 1000
    : completionTimeUtc ? new Date(completionTimeUtc).getTime() : null;

  const diffMs = completionMs != null ? completionMs - now : 0;
  const label = completionMs != null ? formatTimeRemaining(diffMs) : '';
  const finishTime = completionMs != null ? formatCompletionTime(completionMs, now) : '';

  if (completionMs == null) return null;

  return (
    <div className={`flex items-center gap-2 text-xs text-pf-text-secondary mb-2 ${className ?? ''}`}>
      <Badge variant="default" size="sm">⏱ {label}</Badge>
      <span className="opacity-70">done ~{finishTime}</span>
    </div>
  );
}
