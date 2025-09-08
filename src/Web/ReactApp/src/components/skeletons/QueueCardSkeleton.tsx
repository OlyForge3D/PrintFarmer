import React from 'react';

export const QueueCardSkeleton: React.FC = () => (
  <div className="bg-white rounded-lg shadow-md p-6 border border-gray-200 pf-animate-skeleton" aria-busy="true" aria-label="Loading queue overview">
    <div className="flex items-start justify-between mb-4">
      <div className="flex items-center space-x-3">
        <div className="pf-skeleton pf-animate-skeleton w-6 h-6" />
        <div>
          <div className="pf-skeleton pf-animate-skeleton h-4 w-32 mb-2" />
          <div className="pf-skeleton pf-animate-skeleton h-3 w-20" />
        </div>
      </div>
      <div className="pf-skeleton pf-animate-skeleton h-5 w-16" />
    </div>
    <div className="space-y-3">
      <div className="pf-skeleton pf-animate-skeleton h-4 w-24" />
      <div className="pf-skeleton pf-animate-skeleton h-4 w-32" />
      <div className="pf-skeleton pf-animate-skeleton h-4 w-20" />
    </div>
    <div className="mt-4 pt-4 border-t border-gray-200">
      <div className="pf-skeleton pf-animate-skeleton h-9 w-full" />
    </div>
  </div>
);
