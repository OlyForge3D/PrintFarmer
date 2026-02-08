import { useEffect, useState, useCallback } from 'react';
import { Alert } from '@/common/components/ui/Alert';
import { apiClient } from '@/services/api';
import type { TimelineEventDto, DurationAnalyticsDto } from '@/types/api';
import { JobTimeline } from './timing/JobTimeline';
import { DurationComparison } from './timing/DurationComparison';
import { CompletionPrediction } from './timing/CompletionPrediction';

// Compute date range once - stable default for the component
const getDefaultDateRange = () => ({
  from: new Date(Date.now() - 7 * 24 * 60 * 60 * 1000), // Last 7 days
  to: new Date(),
});

export default function TimingTab() {
  const [timelineEvents, setTimelineEvents] = useState<TimelineEventDto[]>([]);
  const [durationAnalytics, setDurationAnalytics] = useState<DurationAnalyticsDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [dateRange, setDateRange] = useState(getDefaultDateRange);

  const loadTimingData = useCallback(async () => {
    try {
      setError(null);
      setLoading(true);

      // Fetch timeline events
      const timeline = await apiClient.getAnalyticsTimeline(
        dateRange.from,
        dateRange.to,
        undefined,
        undefined,
        100
      );
      setTimelineEvents(timeline);

      // Fetch duration analytics
      const analytics = await apiClient.getAnalyticsDurationAnalytics(
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
  }, [dateRange]);

  // Data fetching on mount - wrap in IIFE to make async explicit
  useEffect(() => {
    void (async () => {
      await loadTimingData();
    })();
  }, [loadTimingData]);

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
      <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-4 lg:p-6" role="region" aria-label="Timeline analysis filters">
        <h2 className="text-xl font-semibold text-pf-text-primary mb-4 lg:mb-6">Timeline Analysis</h2>

        <div className="grid grid-cols-1 sm:grid-cols-2 gap-4 lg:gap-6">
          <div>
            <label htmlFor="date-from" className="block text-sm font-medium text-pf-text-primary mb-2">
              From Date
            </label>
            <input
              id="date-from"
              type="date"
              value={dateRange.from.toISOString().split('T')[0]}
              onChange={(e) => handleDateRangeChange('from', new Date(e.target.value))}
              className="w-full px-3 py-2.5 bg-pf-bg-2 border border-pf-border rounded-sm text-pf-text-primary focus:outline-hidden focus:ring-2 focus:ring-pf-accent focus:border-transparent transition-all duration-200"
              aria-label="Select start date for timeline analysis"
            />
          </div>
          <div>
            <label htmlFor="date-to" className="block text-sm font-medium text-pf-text-primary mb-2">
              To Date
            </label>
            <input
              id="date-to"
              type="date"
              value={dateRange.to.toISOString().split('T')[0]}
              onChange={(e) => handleDateRangeChange('to', new Date(e.target.value))}
              className="w-full px-3 py-2.5 bg-pf-bg-2 border border-pf-border rounded-sm text-pf-text-primary focus:outline-hidden focus:ring-2 focus:ring-pf-accent focus:border-transparent transition-all duration-200"
              aria-label="Select end date for timeline analysis"
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
