import { useState, useEffect } from 'react';
import { Badge } from '@/common/components/ui';

interface EstimatedCompletionBadgeProps {
  completionTimeUtc?: string;
  printTimeLeftSeconds?: number;
  className?: string;
}

function formatTimeRemaining(diffMs: number): string {
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

function formatCompletionTime(completionMs: number): string {
  const date = new Date(completionMs);
  return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
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
  const finishTime = completionMs != null ? formatCompletionTime(completionMs) : '';

  if (completionMs == null) return null;

  return (
    <div className={`flex items-center gap-2 text-xs text-pf-text-secondary mb-2 ${className ?? ''}`}>
      <Badge variant="default" size="sm">⏱ {label}</Badge>
      <span className="opacity-70">done ~{finishTime}</span>
    </div>
  );
}
