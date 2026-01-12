import React from 'react';

export const TableSkeleton: React.FC<{ rows?: number; cols?: number; className?: string }> = ({ rows = 5, cols = 5, className }) => {
  const colArray = Array.from({ length: cols });
  const rowArray = Array.from({ length: rows });
  // Tailwind can't generate arbitrary repeat classes dynamically without safelist; fallback to max 8 columns
  const colClass = {
    1: 'grid-cols-1', 2: 'grid-cols-2', 3: 'grid-cols-3', 4: 'grid-cols-4',
    5: 'grid-cols-5', 6: 'grid-cols-6', 7: 'grid-cols-7', 8: 'grid-cols-8'
  }[Math.min(cols, 8)] || 'grid-cols-5';
  return (
    <div className={`border border-gray-200 rounded-lg overflow-hidden ${className ?? ''}`} aria-busy="true" aria-live="polite" aria-label="Loading table">
      <div className="bg-pf-bg-0 border-b border-pf-border px-4 py-3">
        <div className="h-4 w-40 bg-pf-bg-2 rounded animate-pulse" />
      </div>
      <div className="divide-y divide-gray-200">
        {rowArray.map((_, r) => (
          <div key={r} className={`grid ${colClass}`}>
            {colArray.map((__, c) => (
              <div key={c} className="p-3">
                <div className="h-3 w-full bg-pf-bg-1 rounded animate-pulse" />
              </div>
            ))}
          </div>
        ))}
      </div>
    </div>
  );
};
