import React from 'react';
import clsx from 'clsx';

interface InheritanceBadgeProps {
  status: 'inherited' | 'overridden' | 'standalone';
  parentName?: string;
  className?: string;
}

/**
 * Badge showing inheritance status with colored dot indicator
 * - Blue dot = inherited from parent
 * - Orange dot = overridden
 * - Gray dot = standalone (no parent)
 */
export const InheritanceBadge: React.FC<InheritanceBadgeProps> = ({
  status,
  parentName,
  className,
}) => {
  const dotColor = {
    inherited: 'bg-blue-500',
    overridden: 'bg-orange-500',
    standalone: 'bg-gray-400',
  }[status];

  const tooltipText = {
    inherited: parentName ? `Inherited from ${parentName}` : 'Inherited from parent',
    overridden: 'Overridden',
    standalone: 'Standalone profile',
  }[status];

  const showTooltip = status !== 'standalone';

  return (
    <div
      className={clsx('inline-flex items-center gap-1.5', className)}
      title={showTooltip ? tooltipText : undefined}
    >
      <div
        className={clsx('w-2 h-2 rounded-full', dotColor)}
        aria-hidden="true"
      />
      {showTooltip && (
        <span className="sr-only">{tooltipText}</span>
      )}
    </div>
  );
};
