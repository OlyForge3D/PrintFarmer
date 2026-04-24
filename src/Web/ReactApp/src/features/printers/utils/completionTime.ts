export function formatTimeRemaining(diffMs: number): string {
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

export function formatCompletionTime(completionMs: number, nowMs: number): string {
  const completion = new Date(completionMs);
  const now = new Date(nowMs);
  const time = completion.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });

  const todayStart = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
  const tomorrowStart = todayStart + 86_400_000;
  const dayAfterTomorrow = tomorrowStart + 86_400_000;

  if (completionMs >= todayStart && completionMs < tomorrowStart) {
    return time;
  }
  if (completionMs >= tomorrowStart && completionMs < dayAfterTomorrow) {
    return `Tomorrow ${time}`;
  }
  const dateStr = completion.toLocaleDateString([], { month: 'short', day: 'numeric' });
  return `${dateStr}, ${time}`;
}
