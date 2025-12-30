import React from 'react';

interface SkeletonProps {
  lines?: number;
  className?: string;
  variant?: 'rect' | 'pill';
  width?: string | number;
  height?: string | number;
  'aria-label'?: string;
}

export const Skeleton: React.FC<SkeletonProps> = ({
  lines = 1,
  className = '',
  variant = 'rect',
  width,
  height,
  'aria-label': ariaLabel
}) => {
  const items = Array.from({ length: lines });
  const widthClass = typeof width === 'number' ? `w-[${width}px]` : typeof width === 'string' ? '' : 'w-full';
  const heightRem = height ? (typeof height === 'number' ? `${height}px` : height) : (variant === 'pill' ? '0.75rem' : '1rem');
  return (
  <div aria-label={ariaLabel} className={`flex flex-col gap-2 ${className}`} data-skeleton>
      {items.map((_, i) => (
        <div
          key={i}
          className={`skeleton-base ${variant === 'pill' ? 'skeleton-pill' : 'skeleton-rounded'} bg-pf-bg-1 ${widthClass}`}
          data-skeleton-item
          data-sz={heightRem}
        />
      ))}
    </div>
  );
};
