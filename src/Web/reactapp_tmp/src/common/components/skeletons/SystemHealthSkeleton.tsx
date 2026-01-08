import React from 'react';

export const SystemHealthSkeleton: React.FC<{ compact?: boolean; className?: string }> = ({ compact, className }) => {
  if (compact) {
    return (
      <div className={`flex items-center space-x-2 ${className ?? ''}`} aria-busy="true" aria-label="Loading system health">
        <div className="pf-skeleton pf-animate-skeleton h-4 w-4 rounded-full" />
        <div className="pf-skeleton pf-animate-skeleton h-3 w-20" />
      </div>
    );
  }
  return (
    <div className={`bg-white rounded-lg shadow p-6 ${className ?? ''}`} aria-busy="true" aria-label="Loading detailed system health">
      <div className="pf-skeleton pf-animate-skeleton h-5 w-40 mb-4" />
      <div className="space-y-3">
        {Array.from({ length: 4 }).map((_, i) => (
          <div key={i} className="flex items-center justify-between p-3 rounded-lg bg-pf-bg-1">
            <div className="flex items-center space-x-2">
              <div className="pf-skeleton pf-animate-skeleton h-5 w-5 rounded-full" />
              <div className="pf-skeleton pf-animate-skeleton h-4 w-28" />
            </div>
            <div className="pf-skeleton pf-animate-skeleton h-4 w-16" />
          </div>
        ))}
      </div>
      <div className="mt-4 pt-3 border-t border-gray-200">
        <div className="pf-skeleton pf-animate-skeleton h-3 w-32" />
      </div>
    </div>
  );
};
