import type { SpoolmanSpoolDto } from '@/features/filamentManagement/types';

/* ── Filament formatters ────────────────────────────────────────────── */

export const formatTemp = (temp?: number | null): string =>
  temp != null ? `${temp}°C` : '—';

export const formatFilamentWeight = (w?: number | null): string =>
  w != null ? `${w}g` : '—';

export const formatPrice = (p?: number | null): string =>
  p != null ? `$${p.toFixed(2)}` : '—';

export const formatDiameter = (d?: number | null): string =>
  d != null ? `${d}mm` : '—';

/* ── Spool formatters ───────────────────────────────────────────────── */

export const formatSpoolWeight = (weight?: number | null): string => {
  if (typeof weight === 'number' && isFinite(weight))
    return `${Math.max(0, weight).toFixed(0)}g`;
  return '—';
};

export const getUsagePercentage = (spool: SpoolmanSpoolDto): number => {
  if (typeof spool.usedPercent === 'number') return spool.usedPercent;
  if (
    typeof spool.usedWeightG === 'number' &&
    typeof spool.initialWeightG === 'number' &&
    spool.initialWeightG > 0
  ) {
    return (spool.usedWeightG / spool.initialWeightG) * 100;
  }
  if (
    typeof spool.remainingWeightG === 'number' &&
    typeof spool.initialWeightG === 'number' &&
    spool.initialWeightG > 0
  ) {
    return ((spool.initialWeightG - spool.remainingWeightG) / spool.initialWeightG) * 100;
  }
  return 0;
};

export const getRemainingPercentage = (spool: SpoolmanSpoolDto): number => {
  if (typeof spool.remainingPercent === 'number') return spool.remainingPercent;
  const used = getUsagePercentage(spool);
  return used > 0 ? 100 - used : 0;
};

export const weightTooltip = (spool: SpoolmanSpoolDto): string => {
  const parts: string[] = [];
  if (spool.initialWeightG != null) parts.push(`Initial: ${spool.initialWeightG}g`);
  if (spool.remainingWeightG != null) parts.push(`Remaining: ${spool.remainingWeightG}g`);
  const used = spool.usedWeightG ??
    (spool.initialWeightG && spool.remainingWeightG != null
      ? spool.initialWeightG - spool.remainingWeightG
      : undefined);
  if (used != null) parts.push(`Used: ${used}g`);
  parts.push(`Used %: ${getUsagePercentage(spool).toFixed(1)}%`);
  parts.push(`Remaining %: ${getRemainingPercentage(spool).toFixed(1)}%`);
  return parts.join(' | ');
};
