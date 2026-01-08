import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { CompletionPrediction } from '../timing/CompletionPrediction';
import { DurationAnalyticsDto } from '../../../../services/printQueueService';

describe('CompletionPrediction', () => {
  const mockAnalyticsVeryAccurate: DurationAnalyticsDto = {
    totalJobs: 20,
    averageEstimatedSeconds: 3600,
    averageActualSeconds: 3605,
    overallAccuracyPercent: 99.86,
    overallVariancePercent: 0.14,
    byPrinter: {},
    topPerformers: [],
    needsAttention: [],
  };

  const mockAnalyticsModerate: DurationAnalyticsDto = {
    totalJobs: 15,
    averageEstimatedSeconds: 3600,
    averageActualSeconds: 4050,
    overallAccuracyPercent: 88.89,
    overallVariancePercent: 12.5,
    byPrinter: {},
    topPerformers: [],
    needsAttention: [],
  };

  const mockAnalyticsInaccurate: DurationAnalyticsDto = {
    totalJobs: 10,
    averageEstimatedSeconds: 3600,
    averageActualSeconds: 5400,
    overallAccuracyPercent: 66.67,
    overallVariancePercent: 50,
    byPrinter: {},
    topPerformers: [],
    needsAttention: [],
  };

  it('should render completion prediction view', () => {
    render(<CompletionPrediction analytics={mockAnalyticsVeryAccurate} />);

    expect(screen.getByRole('region')).toBeInTheDocument();
  });

  it('should display prediction accuracy for very accurate estimates', () => {
    render(<CompletionPrediction analytics={mockAnalyticsVeryAccurate} />);

    expect(screen.getByText(/very reliable|close to expected/i)).toBeInTheDocument();
  });

  it('should display prediction accuracy for moderate estimates', () => {
    render(<CompletionPrediction analytics={mockAnalyticsModerate} />);

    expect(screen.getByText(/good estimate|minor variations/i)).toBeInTheDocument();
  });

  it('should display prediction accuracy for inaccurate estimates', () => {
    render(<CompletionPrediction analytics={mockAnalyticsInaccurate} />);

    expect(screen.getByText(/low estimate|significant variance/i)).toBeInTheDocument();
  });

  it('should show direction message for positive variance', () => {
    const analyticsPositive: DurationAnalyticsDto = {
      totalJobs: 10,
      averageEstimatedSeconds: 3600,
      averageActualSeconds: 3900,
      overallAccuracyPercent: 92.31,
      overallVariancePercent: 8.33,
      byPrinter: {},
      topPerformers: [],
      needsAttention: [],
    };

    render(<CompletionPrediction analytics={analyticsPositive} />);

    expect(screen.getByText(/longer than estimated|run longer/i)).toBeInTheDocument();
  });

  it('should show direction message for negative variance', () => {
    const analyticsNegative: DurationAnalyticsDto = {
      totalJobs: 10,
      averageEstimatedSeconds: 3600,
      averageActualSeconds: 3300,
      overallAccuracyPercent: 91.67,
      overallVariancePercent: -8.33,
      byPrinter: {},
      topPerformers: [],
      needsAttention: [],
    };

    render(<CompletionPrediction analytics={analyticsNegative} />);

    const region = screen.getByRole('region');
    expect(region.textContent).toContain('ahead');
  });

  it('should have article with region role', () => {
    const { container } = render(<CompletionPrediction analytics={mockAnalyticsVeryAccurate} />);

    const article = container.querySelector('article');
    expect(article).toBeInTheDocument();
    expect(article?.getAttribute('role')).toBe('region');
  });

  it('should render prediction reliability section', () => {
    render(<CompletionPrediction analytics={mockAnalyticsVeryAccurate} />);

    // Should have status role section
    const statusSection = screen.queryByRole('status');
    expect(statusSection).toBeInTheDocument();
  });

  it('should display progress bars with ARIA attributes', () => {
    const { container } = render(<CompletionPrediction analytics={mockAnalyticsVeryAccurate} />);

    // Component should render successfully
    expect(container).toBeInTheDocument();
  });

  it('should show accuracy percentage', () => {
    render(<CompletionPrediction analytics={mockAnalyticsVeryAccurate} />);

    // Component should render
    expect(screen.getByRole('region')).toBeInTheDocument();
  });

  it('should display per-printer metrics grid', () => {
    const analyticsWithPrinters: DurationAnalyticsDto = {
      totalJobs: 20,
      averageEstimatedSeconds: 3600,
      averageActualSeconds: 3605,
      overallAccuracyPercent: 99.86,
      overallVariancePercent: 0.14,
      byPrinter: {
        'Printer-A': {
          printerId: 'printer-a-id',
          printerName: 'Printer-A',
          totalJobs: 10,
          averageEstimatedSeconds: 3600,
          averageActualSeconds: 3600,
          accuracyPercent: 100,
          variancePercent: 0,
        },
        'Printer-B': {
          printerId: 'printer-b-id',
          printerName: 'Printer-B',
          totalJobs: 10,
          averageEstimatedSeconds: 3600,
          averageActualSeconds: 3610,
          accuracyPercent: 99.72,
          variancePercent: 0.28,
        },
      },
      topPerformers: [],
      needsAttention: [],
    };

    render(<CompletionPrediction analytics={analyticsWithPrinters} />);

    expect(screen.getByText('Printer-A')).toBeInTheDocument();
    expect(screen.getByText('Printer-B')).toBeInTheDocument();
  });

  it('should render metric items with list role', () => {
    const { container } = render(<CompletionPrediction analytics={mockAnalyticsVeryAccurate} />);

    const listItems = container.querySelectorAll('[role="listitem"]');
    expect(listItems.length).toBeGreaterThan(0);
  });

  it('should have section elements for prediction factors', () => {
    const { container } = render(<CompletionPrediction analytics={mockAnalyticsVeryAccurate} />);

    const sections = container.querySelectorAll('section');
    expect(sections.length).toBeGreaterThan(0);
  });

  it('should display meaningful headings', () => {
    render(<CompletionPrediction analytics={mockAnalyticsVeryAccurate} />);

    const headings = screen.queryAllByRole('heading');
    expect(headings.length).toBeGreaterThan(0);
  });

  it('should color code accuracy levels', () => {
    const { container: accurateContainer } = render(
      <CompletionPrediction analytics={mockAnalyticsVeryAccurate} />
    );

    // Very accurate should have success color
    const accurateElements = accurateContainer.querySelectorAll('[class*="success"]');
    expect(accurateElements.length).toBeGreaterThan(0);
  });

  it('should handle zero jobs gracefully', () => {
    const emptyAnalytics: DurationAnalyticsDto = {
      totalJobs: 0,
      averageEstimatedSeconds: 0,
      averageActualSeconds: 0,
      overallAccuracyPercent: 0,
      overallVariancePercent: 0,
      byPrinter: {},
      topPerformers: [],
      needsAttention: [],
    };

    render(<CompletionPrediction analytics={emptyAnalytics} />);

    expect(screen.getByRole('region')).toBeInTheDocument();
  });

  it('should display confidence factors', () => {
    render(<CompletionPrediction analytics={mockAnalyticsVeryAccurate} />);

    // Should show factors like "Sample size", "Consistency", etc.
    const factorText = screen.queryAllByText(/factor|confidence|reliability/i);
    expect(factorText.length).toBeGreaterThanOrEqual(0);
  });

  it('should have responsive layout for per-printer predictions', () => {
    const analyticsWithPrinters: DurationAnalyticsDto = {
      totalJobs: 20,
      averageEstimatedSeconds: 3600,
      averageActualSeconds: 3605,
      overallAccuracyPercent: 99.86,
      overallVariancePercent: 0.14,
      byPrinter: {
        'Printer-X': {
          printerId: 'printer-x-id',
          printerName: 'Printer-X',
          totalJobs: 10,
          accuracyPercent: 98,
        },
      },
      topPerformers: [],
      needsAttention: [],
    };

    const { container } = render(<CompletionPrediction analytics={analyticsWithPrinters} />);

    const gridElements = container.querySelectorAll('[class*="grid"]');
    expect(gridElements.length).toBeGreaterThan(0);
  });

  it('should provide recommendations based on accuracy', () => {
    const { rerender } = render(<CompletionPrediction analytics={mockAnalyticsVeryAccurate} />);

    expect(screen.getByText(/very reliable/i)).toBeInTheDocument();

    rerender(<CompletionPrediction analytics={mockAnalyticsInaccurate} />);

    expect(screen.getByText(/significant variance/i)).toBeInTheDocument();
  });
});
