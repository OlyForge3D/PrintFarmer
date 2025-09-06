import { useEffect, useRef } from 'react';

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
  const wrapperRef = useRef<HTMLDivElement | null>(null);
  const total = usedWeight + remainingWeight;
  const usedPct = total === 0 ? 0 : (usedWeight / total) * 100; // percentage used
  const remainingPct = 100 - usedPct;

  useEffect(() => {
    if (wrapperRef.current) {
      // We display remaining portion as width for visual emphasis of what is left
      wrapperRef.current.style.setProperty('--used-pct', Math.max(0, Math.min(100, remainingPct)) + '%');
    }
  }, [remainingPct]);

  const colorClass = remainingWeight <= criticalThreshold
    ? 'bg-red-500'
    : remainingWeight <= lowThreshold
      ? 'bg-orange-500'
      : 'bg-blue-500';

  return (
    <div
      ref={wrapperRef}
      className="spool-usage-wrapper w-full bg-pf-bg-0 rounded-full h-2 overflow-hidden"
      role="progressbar"
      aria-label={label + ' ' + usedPct.toFixed(1) + '% used'}
      data-min="0"
      data-max="100"
      data-now={usedPct.toFixed(1)}
    >
      <div className={`spool-usage-fill h-full transition-all duration-500 ease-out ${colorClass}`} />
      <span className="sr-only">{usedPct.toFixed(1)}% used</span>
    </div>
  );
}
