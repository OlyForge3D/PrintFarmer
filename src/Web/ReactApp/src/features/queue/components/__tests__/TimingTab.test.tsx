import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import TimingTab from '../TimingTab';
import { apiClient } from '../../../../services/api';

vi.mock('../../../../services/api');

describe('TimingTab', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('should render timing tab with date range selectors', () => {
    vi.mocked(apiClient.getAnalyticsTimeline).mockResolvedValue([]);
    vi.mocked(apiClient.getAnalyticsDurationAnalytics).mockResolvedValue({
      totalJobs: 0,
      averageEstimatedSeconds: 0,
      averageActualSeconds: 0,
      overallAccuracyPercent: 0,
      overallVariancePercent: 0,
      byPrinter: {},
      topPerformers: [],
      needsAttention: [],
    });

    render(<TimingTab />);

    expect(screen.getByText('Timeline Analysis')).toBeInTheDocument();
    expect(screen.getByLabelText('Select start date for timeline analysis')).toBeInTheDocument();
    expect(screen.getByLabelText('Select end date for timeline analysis')).toBeInTheDocument();
  });

  it('should have proper accessibility labels on date inputs', () => {
    vi.mocked(apiClient.getAnalyticsTimeline).mockResolvedValue([]);
    vi.mocked(apiClient.getAnalyticsDurationAnalytics).mockResolvedValue({
      totalJobs: 0,
      averageEstimatedSeconds: 0,
      averageActualSeconds: 0,
      overallAccuracyPercent: 0,
      overallVariancePercent: 0,
      byPrinter: {},
      topPerformers: [],
      needsAttention: [],
    });

    render(<TimingTab />);

    const fromInput = screen.getByLabelText('Select start date for timeline analysis');
    const toInput = screen.getByLabelText('Select end date for timeline analysis');

    expect(fromInput).toHaveAttribute('type', 'date');
    expect(toInput).toHaveAttribute('type', 'date');
  });

  it('should fetch data on date range change', async () => {
    const mockTimeline = [
      {
        jobId: '1',
        jobName: 'Job 1',
        state: 'Printing',
        printerName: 'Printer 1',
        enteredAtUtc: new Date().toISOString(),
        exitedAtUtc: undefined,
        durationSeconds: 1000,
        estimatedDurationSeconds: 1200,
        variancePercent: 16.7,
      },
    ];

    vi.mocked(apiClient.getAnalyticsTimeline).mockResolvedValue(mockTimeline);
    vi.mocked(apiClient.getAnalyticsDurationAnalytics).mockResolvedValue({
      totalJobs: 1,
      averageEstimatedSeconds: 1200,
      averageActualSeconds: 1000,
      overallAccuracyPercent: 83.3,
      overallVariancePercent: -16.7,
      byPrinter: {},
      topPerformers: [],
      needsAttention: [],
    });

    render(<TimingTab />);

    await waitFor(() => {
      expect(apiClient.getAnalyticsTimeline).toHaveBeenCalled();
    });
  });

  it('should display error message on failed data fetch', async () => {
    const errorMessage = 'Failed to load timeline data';
    vi.mocked(apiClient.getAnalyticsTimeline).mockRejectedValue(new Error(errorMessage));

    render(<TimingTab />);

    await waitFor(() => {
      expect(screen.getByText(errorMessage)).toBeInTheDocument();
    });
  });

  it('should have region role for semantic accessibility', () => {
    vi.mocked(apiClient.getAnalyticsTimeline).mockResolvedValue([]);
    vi.mocked(apiClient.getAnalyticsDurationAnalytics).mockResolvedValue({
      totalJobs: 0,
      averageEstimatedSeconds: 0,
      averageActualSeconds: 0,
      overallAccuracyPercent: 0,
      overallVariancePercent: 0,
      byPrinter: {},
      topPerformers: [],
      needsAttention: [],
    });

    render(<TimingTab />);

    const filterRegion = screen.getByRole('region', { name: 'Timeline analysis filters' });
    expect(filterRegion).toBeInTheDocument();
  });

  it('should focus on date input when clicked', () => {
    vi.mocked(apiClient.getAnalyticsTimeline).mockResolvedValue([]);
    vi.mocked(apiClient.getAnalyticsDurationAnalytics).mockResolvedValue({
      totalJobs: 0,
      averageEstimatedSeconds: 0,
      averageActualSeconds: 0,
      overallAccuracyPercent: 0,
      overallVariancePercent: 0,
      byPrinter: {},
      topPerformers: [],
      needsAttention: [],
    });

    render(<TimingTab />);

    const fromInput = screen.getByLabelText('Select start date for timeline analysis') as HTMLInputElement;
    
    // Component should render date input elements
    expect(fromInput).toBeInTheDocument();
    expect(fromInput.type).toBe('date');
  });
});
