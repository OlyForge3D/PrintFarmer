/**
 * Job Statistics Panel Component (Phase 4.2)
 * Displays material and model statistics for analytics
 */

import React, { useState } from 'react';
import { Button } from '@/common/components/ui/Button';
import { useMaterialStats, useModelStats } from '@/hooks/usePredictions';
import type { DurationStatsDto } from '@/types/predictions';

interface JobStatisticsPanelProps {
  material?: string;
  printerId?: string;
  modelId?: string;
}

export const JobStatisticsPanel: React.FC<JobStatisticsPanelProps> = ({
  material,
  printerId,
  modelId,
}) => {
  const [view, setView] = useState<'material' | 'model'>('material');

  const materialQuery = useMaterialStats(material, printerId, view === 'material');
  const modelQuery = useModelStats(modelId, material, view === 'model');

  const isLoading = view === 'material' ? materialQuery.isLoading : modelQuery.isLoading;
  const error = view === 'material' ? materialQuery.error : modelQuery.error;

  const formatDuration = (isoDuration: string): string => {
    try {
      const match = isoDuration.match(/PT(?:(\d+)H)?(?:(\d+)M)?(?:(\d+(?:\.\d{1,3})?)S)?/);
      if (!match) return isoDuration;

      const hours = parseInt(match[1] || '0', 10);
      const minutes = parseInt(match[2] || '0', 10);
      const seconds = parseInt(match[3] || '0', 10);

      const parts = [];
      if (hours > 0) parts.push(`${hours}h`);
      if (minutes > 0) parts.push(`${minutes}m`);
      if (seconds > 0 && hours === 0) parts.push(`${Math.round(seconds)}s`);

      return parts.length > 0 ? parts.join(' ') : '<1m';
    } catch {
      return isoDuration;
    }
  };

  const renderStats = (stats: DurationStatsDto | null) => {
    if (!stats) {
      return <div className="text-sm pf-text-secondary">No data available</div>;
    }

    const successRate = (stats.successRate * 100).toFixed(1);

    return (
      <div className="space-y-4">
        {/* Overview */}
        <div className="grid grid-cols-2 gap-4">
          <div className="pf-bg-0 pf-border rounded p-3">
            <div className="pf-text-secondary text-xs">Total Jobs</div>
            <div className="text-lg font-semibold pf-text-primary">{stats.totalJobs}</div>
          </div>
          <div className="pf-bg-0 pf-border rounded p-3">
            <div className="pf-text-secondary text-xs">Success Rate</div>
            <div className="text-lg font-semibold pf-success">{successRate}%</div>
          </div>
        </div>

        {/* Duration Statistics */}
        <div className="pf-bg-0 pf-border rounded p-4">
          <h5 className="font-semibold pf-text-primary mb-3 text-sm">Duration Statistics</h5>
          <div className="space-y-2 text-sm">
            <div className="flex justify-between items-center">
              <span className="pf-text-secondary">Average</span>
              <span className="font-mono pf-text-primary">{formatDuration(stats.averageDuration)}</span>
            </div>
            <div className="flex justify-between items-center">
              <span className="pf-text-secondary">Median</span>
              <span className="font-mono pf-text-primary">{formatDuration(stats.medianDuration)}</span>
            </div>
            <div className="flex justify-between items-center">
              <span className="pf-text-secondary">Min</span>
              <span className="font-mono pf-text-primary">{formatDuration(stats.minDuration)}</span>
            </div>
            <div className="flex justify-between items-center">
              <span className="pf-text-secondary">Max</span>
              <span className="font-mono pf-text-primary">{formatDuration(stats.maxDuration)}</span>
            </div>
          </div>
        </div>

        {/* Variance */}
        <div className="pf-bg-0 pf-border rounded p-3">
          <div className="pf-text-secondary text-xs">Standard Deviation</div>
          <div className="text-lg font-semibold pf-text-primary">
            {(stats.standardDeviation / 1000).toFixed(2)}s
          </div>
        </div>
      </div>
    );
  };

  return (
    <div className="pf-card pf-bg-1 pf-border rounded-lg">
      {/* Header */}
      <div className="p-4 border-b pf-border">
        <h4 className="pf-text-primary font-semibold flex items-center gap-2">
          <span className="text-lg">📊</span>
          Job Statistics
        </h4>
      </div>

      {/* View Toggle */}
      <div className="flex gap-2 p-4 border-b pf-border">
        <Button
          onClick={() => setView('material')}
          variant={view === 'material' ? 'primary' : 'subtle'}
          size="sm"
          aria-label="View statistics by material"
        >
          By Material
        </Button>
        {modelId && (
          <Button
            onClick={() => setView('model')}
            variant={view === 'model' ? 'primary' : 'subtle'}
            size="sm"
            aria-label="View statistics by model"
          >
            By Model
          </Button>
        )}
      </div>

      {/* Content */}
      <div className="p-4">
        {isLoading && <div className="text-sm pf-text-secondary">Loading statistics...</div>}
        {error && <div className="text-sm text-red-600">Failed to load statistics</div>}
        {!isLoading && !error && (
          <>
            {view === 'material' && materialQuery.data && (
              <div>
                {Object.keys(materialQuery.data).length === 0 ? (
                  <div className="text-sm pf-text-secondary">No material data available</div>
                ) : (
                  <div className="space-y-4">
                    {Object.entries(materialQuery.data).map(([mat, stats]) => (
                      <div key={mat} className="border-t pf-border pt-4 first:border-t-0 first:pt-0">
                        <h5 className="font-semibold pf-text-primary text-sm mb-3">{mat}</h5>
                        {renderStats(stats)}
                      </div>
                    ))}
                  </div>
                )}
              </div>
            )}
            {view === 'model' && modelQuery.data && (
              <div>{renderStats(modelQuery.data)}</div>
            )}
          </>
        )}
      </div>
    </div>
  );
};
