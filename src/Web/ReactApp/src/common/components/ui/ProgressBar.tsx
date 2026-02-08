import React from 'react';
import clsx from 'clsx';

export interface ProgressBarProps {
  value: number; // 0-100
  label?: string;
  size?: 'xs' | 'sm' | 'md';
  color?: 'blue' | 'green' | 'purple' | 'red' | 'gray';
  showPercent?: boolean;
  animated?: boolean;
  className?: string;
}

const heightMap = {
  xs: 'h-1',
  sm: 'h-2',
  md: 'h-3'
};

const colorMap: Record<string, string> = {
  blue: 'bg-pf-accent',
  green: 'bg-pf-success-bg',
  purple: 'bg-linear-to-r from-pf-gradient-secondary-start to-pf-gradient-secondary-end',
  red: 'bg-pf-error',
  gray: 'bg-pf-text-muted'
};

export const ProgressBar: React.FC<ProgressBarProps> = ({
  value,
  label,
  size = 'sm',
  color = 'blue',
  showPercent = true,
  animated = true,
  className
}) => {
  const pct = Math.min(100, Math.max(0, Math.round(value)));
  return (
    <div className={clsx('w-full', className)}>
      {(label || showPercent) && (
        <div className="flex justify-between text-xs mb-1 text-pf-text-secondary">
          {label && <span>{label}</span>}
          {showPercent && <span>{pct}%</span>}
        </div>
      )}
      <div
        className={clsx('w-full bg-pf-bg-1 rounded-full overflow-hidden', heightMap[size])}
        role="progressbar"
        aria-valuenow={pct}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-label={label || 'Progress'}
      >
        <div
          className={clsx(
            colorMap[color], 
            'h-full rounded-full',
            animated && 'transition-[width] duration-200 ease-out'
          )}
          style={{ width: `${pct}%` }}
        />
      </div>
    </div>
  );
};

export default ProgressBar;
