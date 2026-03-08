import React from 'react';

export const ChartSkeleton: React.FC<{ className?: string }> = ({ className }) => (
  <div className={`flex h-full items-center justify-center ${className ?? ''}`} aria-busy="true" aria-label="Loading chart">
    <div className="w-full space-y-3 px-4">
      <div className="flex items-end gap-2 h-32">
        {[40, 65, 50, 80, 60, 45, 70, 55].map((h, i) => (
          <div
            key={i}
            className="flex-1 pf-skeleton pf-animate-skeleton rounded-t-sm"
            style={{ height: `${h}%` }}
          />
        ))}
      </div>
      <div className="pf-skeleton pf-animate-skeleton h-3 w-full rounded-sm" />
    </div>
  </div>
);
