import React from 'react';
import clsx from 'clsx';

export interface ProgressBarProps {
  value: number;
  max?: number;
  label?: string;
  ariaLabel?: string;
  ariaValueText?: string;
  size?: 'xs' | 'sm' | 'md';
  showPercent?: boolean;
  animated?: boolean;
  className?: string;
  trackClassName?: string;
  fillClassName?: string;
  fillRef?: React.Ref<HTMLDivElement>;
}

const heightMap = {
  xs: 'h-1',
  sm: 'h-2',
  md: 'h-3'
};

const trackClass = 'bg-pf-progress-track';
const fillClass = 'bg-pf-progress-fill';

export const ProgressBar: React.FC<ProgressBarProps> = ({
  value,
  max = 100,
  label,
  ariaLabel,
  ariaValueText,
  size = 'sm',
  showPercent = true,
  animated = true,
  className,
  trackClassName,
  fillClassName,
  fillRef,
}) => {
  const safeMax = Math.max(1, max);
  const clampedValue = Math.min(safeMax, Math.max(0, value));
  const pct = Math.min(100, Math.max(0, Math.round((clampedValue / safeMax) * 100)));
  return (
    <div className={clsx('w-full', className)}>
      {(label || showPercent) && (
        <div className="flex justify-between text-xs mb-1 text-pf-text-secondary">
          {label && <span>{label}</span>}
          {showPercent && <span>{pct}%</span>}
        </div>
      )}
      <div
        data-pf-progress-track
        className={clsx('w-full rounded-full overflow-hidden', trackClass, heightMap[size], trackClassName)}
        role="progressbar"
        aria-valuenow={Math.round(clampedValue)}
        aria-valuemin={0}
        aria-valuemax={safeMax}
        aria-valuetext={ariaValueText}
        aria-label={ariaLabel || label || 'Progress'}
      >
        <div
          ref={fillRef}
          data-pf-progress-fill
          className={clsx(
            'h-full rounded-full',
            !fillClassName && fillClass,
            animated && 'transition-[width] duration-200 ease-out',
            fillClassName
          )}
          style={{ width: `${pct}%` }}
        />
      </div>
    </div>
  );
};

export default ProgressBar;
