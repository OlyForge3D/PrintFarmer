import { useEffect, useState } from 'react';
import { Alert } from '@/common/components/ui/Alert';
import { printQueueService, TimelineEventDto, DurationAnalyticsDto } from '@/services/printQueueService';
import { JobTimeline } from './timing/JobTimeline';
import { DurationComparison } from './timing/DurationComparison';
import { CompletionPrediction } from './timing/CompletionPrediction';

export default function TimingTab() {
  const [timelineEvents, setTimelineEvents] = useState<TimelineEventDto[]>([]);
  const [durationAnalytics, setDurationAnalytics] = useState<DurationAnalyticsDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [dateRange, setDateRange] = useState({
    from: new Date(Date.now() - 7 * 24 * 60 * 60 * 1000), // Last 7 days
    to: new Date(),
  });

  const loadTimingData = async () => {
    try {
      setError(null);
      setLoading(true);

      // Fetch timeline events
      const timeline = await printQueueService.getTimelineAsync(
        dateRange.from,
        dateRange.to,
        undefined,
        undefined,
        100
      );
      setTimelineEvents(timeline);

      // Fetch duration analytics
      const analytics = await printQueueService.getDurationAnalyticsAsync(
        undefined,
        dateRange.from,
        dateRange.to
      );
      setDurationAnalytics(analytics);

      setLoading(false);
    } catch (err) {
      const errorMessage = err instanceof Error ? err.message : 'Failed to load timing data';
      setError(errorMessage);
      setLoading(false);
    }
  };

  useEffect(() => {
    loadTimingData();
  }, [dateRange]);

  const handleDateRangeChange = (type: 'from' | 'to', date: Date) => {
    setDateRange((prev) => ({
      ...prev,
      [type]: date,
    }));
  };

  return (
    <div className="space-y-6">
      {error && <Alert type="error">{error}</Alert>}

      {/* Date Range Selector */}
      <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-4">
        <h3 className="text-lg font-semibold text-pf-text-primary mb-4">Timeline Analysis</h3>

        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <div>
            <label className="block text-sm font-medium text-pf-text-secondary mb-2">
              From Date
            </label>
            <input
              type="date"
              value={dateRange.from.toISOString().split('T')[0]}
              onChange={(e) => handleDateRangeChange('from', new Date(e.target.value))}
              className="w-full px-3 py-2 bg-pf-bg-2 border border-pf-border rounded text-pf-text-primary"
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-pf-text-secondary mb-2">
              To Date
            </label>
            <input
              type="date"
              value={dateRange.to.toISOString().split('T')[0]}
              onChange={(e) => handleDateRangeChange('to', new Date(e.target.value))}
              className="w-full px-3 py-2 bg-pf-bg-2 border border-pf-border rounded text-pf-text-primary"
            />
          </div>
        </div>
      </div>

      {loading ? (
        <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-8 text-center">
          <div className="inline-block">
            <div className="animate-spin rounded-full h-8 w-8 border-2 border-pf-primary border-t-transparent"></div>
          </div>
          <p className="mt-4 text-pf-text-secondary">Loading timing data...</p>
        </div>
      ) : (
        <>
          {/* Timeline Events */}
          <JobTimeline events={timelineEvents} />

          {/* Duration Analytics */}
          {durationAnalytics && <DurationComparison analytics={durationAnalytics} />}

          {/* Completion Prediction */}
          {durationAnalytics && <CompletionPrediction analytics={durationAnalytics} />}
        </>
      )}
    </div>
  );
}
