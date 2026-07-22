import { ProgressBar } from '@/common/components/ui/ProgressBar';

interface SpoolUsageBarProps {
  usedWeight: number;      // grams used
  remainingWeight: number; // grams remaining
  lowThreshold?: number;   // grams -> warning (orange)
  criticalThreshold?: number; // grams -> critical (red)
  label?: string;
}

export function SpoolUsageBar({
  usedWeight,
  remainingWeight,
  lowThreshold = 50,
  criticalThreshold = 10,
  label = 'Spool usage'
}: SpoolUsageBarProps) {
  const total = usedWeight + remainingWeight;
  const usedPct = total === 0 ? 0 : (usedWeight / total) * 100; // percentage used
  const remainingPct = 100 - usedPct;

  const thresholdFillClass = remainingWeight <= criticalThreshold
    ? 'bg-pf-error'
    : remainingWeight <= lowThreshold
      ? 'bg-pf-warning'
      : undefined;

  return (
    <ProgressBar
      value={remainingPct}
      ariaLabel={label}
      ariaValueText={`${usedPct.toFixed(1)}% used; ${remainingPct.toFixed(1)}% remaining`}
      showPercent={false}
      fillClassName={thresholdFillClass}
      animated
    />
  );
}
