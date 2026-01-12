import React from 'react';

export const PrinterCardSkeleton: React.FC = () => (
  <div className="bg-pf-bg-1 border border-pf-border rounded-xl shadow-lg overflow-hidden pf-animate-skeleton" aria-busy="true" aria-label="Loading printer">
    <div className="h-40 bg-pf-bg-2 pf-skeleton pf-animate-skeleton" />
    <div className="p-4 space-y-3">
      <div className="pf-skeleton pf-animate-skeleton h-4 w-40" />
      <div className="pf-skeleton pf-animate-skeleton h-3 w-24" />
      <div className="flex space-x-2 pt-2">
        <div className="pf-skeleton pf-animate-skeleton h-8 flex-1" />
        <div className="pf-skeleton pf-animate-skeleton h-8 flex-1" />
      </div>
    </div>
  </div>
);
