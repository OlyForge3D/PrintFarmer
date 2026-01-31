/**
 * Completion Prediction Card Component (Phase 4.2)
 * Displays predicted completion time with confidence level
 */

import React from 'react';
import { useCompletionPrediction } from '@/hooks/usePredictions';
import { Button } from '@/common/components/ui/Button';
import type { ConfidenceLevel } from '@/types/predictions';

interface CompletionPredictionCardProps {
  jobId: string | null | undefined;
  compact?: boolean;
  onRefresh?: () => void;
}

export const CompletionPredictionCard: React.FC<CompletionPredictionCardProps> = ({
  jobId,
  compact = false,
  onRefresh,
}) => {
  const { data: prediction, isLoading, error, refetch } = useCompletionPrediction(jobId);

  const getConfidenceColor = (confidence: ConfidenceLevel): string => {
    switch (confidence) {
      case 'High':
        return 'bg-pf-success-bg text-pf-success';
      case 'Medium':
        return 'bg-yellow-100 text-yellow-700';
      case 'Low':
        return 'bg-pf-error-bg text-pf-error-text';
      default:
        return 'bg-pf-bg-1 text-pf-text-primary';
    }
  };

  const getConfidenceIcon = (confidence: ConfidenceLevel): string => {
    switch (confidence) {
      case 'High':
        return '🟢';
      case 'Medium':
        return '🟡';
      case 'Low':
        return '🔴';
      default:
        return '⚪';
    }
  };

  const formatTime = (isoTime: string): string => {
    try {
      const date = new Date(isoTime);
      return date.toLocaleString('en-US', {
        month: 'short',
        day: 'numeric',
        hour: '2-digit',
        minute: '2-digit',
      });
    } catch {
      return isoTime;
    }
  };

  const formatDuration = (isoDuration: string | null): string => {
    if (!isoDuration) return 'Unknown';
    
    // Parse ISO 8601 duration: PT[n]H[n]M[n]S
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
  };

  if (isLoading) {
    return (
      <div className={compact ? 'flex items-center gap-2 text-sm' : 'pf-card pf-bg-1 pf-border'}>
        <div className={compact ? 'animate-pulse h-4 w-32 pf-bg-0 rounded-sm' : 'p-4'}>
          {!compact && <div className="text-sm pf-text-secondary">Loading prediction...</div>}
        </div>
      </div>
    );
  }

  if (error || !prediction) {
    return (
      <div className={compact ? 'text-sm' : 'pf-card pf-bg-1 pf-border'}>
        {!compact && (
          <div className="p-4">
            <div className="text-sm pf-text-secondary">Could not load prediction</div>
          </div>
        )}
        {compact && <span className="text-xs pf-text-secondary">No prediction</span>}
      </div>
    );
  }

  if (compact) {
    return (
      <div className="flex items-center gap-2 text-sm">
        <span className="text-xs pf-text-secondary">Est. Done:</span>
        <span className="font-semibold pf-text-primary">{formatTime(prediction.estimatedCompletionTime)}</span>
        <span className="text-xs">{getConfidenceIcon(prediction.confidence)}</span>
      </div>
    );
  }

  return (
    <div className="pf-card pf-bg-1 pf-border rounded-lg">
      {/* Header */}
      <div className="flex items-center justify-between p-4 border-b pf-border">
        <h4 className="pf-text-primary font-semibold flex items-center gap-2">
          <span className="text-lg">⏱️</span>
          Predicted Completion
        </h4>
        {onRefresh && (
          <Button
            variant="secondary"
            size="sm"
            onClick={() => {
              refetch();
              onRefresh();
            }}
          >
            Refresh
          </Button>
        )}
      </div>

      {/* Content */}
      <div className="p-4 space-y-4">
        {/* Estimated Time */}
        <div>
          <div className="flex items-center justify-between mb-2">
            <span className="pf-text-secondary text-sm">Estimated Completion</span>
            <span className={`text-xs px-2 py-1 rounded-sm ${getConfidenceColor(prediction.confidence)}`}>
              {getConfidenceIcon(prediction.confidence)} {prediction.confidence} Confidence
            </span>
          </div>
          <div className="text-lg font-semibold pf-text-primary">
            {formatTime(prediction.estimatedCompletionTime)}
          </div>
          <div className="text-sm pf-text-secondary mt-1">
            Estimated duration: <span className="font-mono pf-text-primary">{formatDuration(prediction.estimatedDuration)}</span>
          </div>
        </div>

        {/* Statistics */}
        <div className="pt-2 border-t pf-border">
          <div className="grid grid-cols-2 gap-4 text-sm">
            <div>
              <div className="pf-text-secondary text-xs">Sample Size</div>
              <div className="font-semibold pf-text-primary">{prediction.sampleSize} jobs</div>
            </div>
            <div>
              <div className="pf-text-secondary text-xs">Accuracy Range</div>
              <div className="font-semibold pf-text-primary">±{prediction.variancePercent || 50}%</div>
            </div>
          </div>
        </div>

        {/* Note */}
        {prediction.note && (
          <div className="bg-pf-bg-0 pf-border rounded-sm p-3 text-xs pf-text-secondary">
            💡 {prediction.note}
          </div>
        )}
      </div>
    </div>
  );
};
