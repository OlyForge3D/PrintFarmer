import React from 'react';

export const FormSkeleton: React.FC<{ fields?: number; className?: string; actionBar?: boolean }> = ({ fields = 4, className, actionBar = true }) => {
  return (
    <div className={className ?? ''} aria-busy="true" aria-label="Loading form">
      <div className="space-y-4">
        {Array.from({ length: fields }).map((_, i) => (
          <div key={i} className="space-y-2">
            <div className="pf-skeleton pf-animate-skeleton h-3 w-32" />
            <div className="pf-skeleton pf-animate-skeleton h-10 w-full" />
          </div>
        ))}
        {actionBar && (
          <div className="flex justify-end pt-2">
            <div className="pf-skeleton pf-animate-skeleton h-10 w-32" />
          </div>
        )}
      </div>
    </div>
  );
};
