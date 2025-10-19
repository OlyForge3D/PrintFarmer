import React from 'react';
import clsx from 'clsx';
import styles from './ProgressBar.module.css';

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
  green: 'bg-pf-success',
  purple: 'bg-gradient-to-r from-pf-gradient-secondary-start to-pf-gradient-secondary-end',
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
        aria-label={label || 'Progress'}
      >
        {/* Using data-width attribute so CSS can be leveraged if inline styles are disallowed */}
        <div
          className={clsx(colorMap[color], 'h-full transition-all duration-300', animated && 'animate-none', styles['progressbar-fill'])}
          data-width={pct}
        />
      </div>
    </div>
  );
};

export default ProgressBar;
