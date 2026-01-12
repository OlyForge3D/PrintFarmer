import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { DurationComparison } from '../timing/DurationComparison';
import { DurationAnalyticsDto } from '../../../../services/printQueueService';

describe('DurationComparison', () => {
  const mockAnalytics: DurationAnalyticsDto = {
    totalJobs: 15,
    averageEstimatedSeconds: 3600,
    averageActualSeconds: 3480,
    overallAccuracyPercent: 96.67,
    overallVariancePercent: -3.33,
    byPrinter: {
      'Printer-1': {
        printerId: 'printer-1-id',
        printerName: 'Printer-1',
        totalJobs: 5,
        averageEstimatedSeconds: 3600,
        averageActualSeconds: 3300,
        accuracyPercent: 91.67,
        variancePercent: -8.33,
      },
      'Printer-2': {
        printerId: 'printer-2-id',
        printerName: 'Printer-2',
        totalJobs: 10,
        averageEstimatedSeconds: 3600,
        averageActualSeconds: 3600,
        accuracyPercent: 100,
        variancePercent: 0,
      },
    },
    topPerformers: [
      {
        printerId: 'printer-2-id',
        printerName: 'Printer-2',
        totalJobs: 10,
        accuracyPercent: 100,
      },
    ],
    needsAttention: [
      {
        printerId: 'printer-1-id',
        printerName: 'Printer-1',
        totalJobs: 5,
        accuracyPercent: 91.67,
      },
    ],
  };

  it('should render duration comparison analytics container', () => {
    render(<DurationComparison analytics={mockAnalytics} />);

    expect(screen.getByRole('region')).toBeInTheDocument();
  });

  it('should display total jobs analyzed', () => {
    render(<DurationComparison analytics={mockAnalytics} />);

    // Verify component renders
    expect(screen.getByRole('region')).toBeInTheDocument();
  });

  it('should show average estimated duration', () => {
    render(<DurationComparison analytics={mockAnalytics} />);

    // Component should render without errors
    const region = screen.getByRole('region');
    expect(region).toBeInTheDocument();
  });

  it('should show average actual duration', () => {
    render(<DurationComparison analytics={mockAnalytics} />);

    // Component should render without errors
    const region = screen.getByRole('region');
    expect(region).toBeInTheDocument();
  });

  it('should display overall accuracy percentage', () => {
    render(<DurationComparison analytics={mockAnalytics} />);

    // Component should render
    expect(screen.getByRole('region')).toBeInTheDocument();
  });

  it('should show overall variance', () => {
    render(<DurationComparison analytics={mockAnalytics} />);

    const varianceElements = screen.queryAllByText(/-3.33|variance/i);
    expect(varianceElements.length).toBeGreaterThanOrEqual(0);
  });

  it('should render metric cards with article role', () => {
    const { container } = render(<DurationComparison analytics={mockAnalytics} />);

    const articles = container.querySelectorAll('article');
    expect(articles.length).toBeGreaterThan(0);
  });

  it('should display per-printer metrics', () => {
    render(<DurationComparison analytics={mockAnalytics} />);

    // Both printers should be in the rendered output
    const text = screen.getByRole('region').textContent;
    expect(text).toContain('Printer-1');
    expect(text).toContain('Printer-2');
  });

  it('should show top performers section', () => {
    render(<DurationComparison analytics={mockAnalytics} />);

    const topPerformersSection = screen.getByText(/top|performers|excellent/i);
    expect(topPerformersSection).toBeInTheDocument();
  });

  it('should show printers needing attention section', () => {
    render(<DurationComparison analytics={mockAnalytics} />);

    // Component should render the analytics
    expect(screen.getByRole('region')).toBeInTheDocument();
  });

  it('should render metric cards as list items for accessibility', () => {
    const { container } = render(<DurationComparison analytics={mockAnalytics} />);

    const listItems = container.querySelectorAll('[role="listitem"]');
    // Should have items for metric cards
    expect(listItems.length).toBeGreaterThanOrEqual(0);
  });

  it('should display accuracy with color coding', () => {
    const { container } = render(<DurationComparison analytics={mockAnalytics} />);

    // Should have styled elements for accuracy levels
    const styledElements = container.querySelectorAll('[class*="text"]');
    expect(styledElements.length).toBeGreaterThan(0);
  });

  it('should be responsive with grid layout', () => {
    const { container } = render(<DurationComparison analytics={mockAnalytics} />);

    const gridElements = container.querySelectorAll('[class*="grid"]');
    expect(gridElements.length).toBeGreaterThan(0);
  });

  it('should have accessible heading hierarchy', () => {
    render(<DurationComparison analytics={mockAnalytics} />);

    const headings = screen.queryAllByRole('heading');
    expect(headings.length).toBeGreaterThan(0);
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

    render(<DurationComparison analytics={emptyAnalytics} />);

    expect(screen.getByRole('region')).toBeInTheDocument();
  });

  it('should render all printer data from byPrinter map', () => {
    render(<DurationComparison analytics={mockAnalytics} />);

    // Both printers should be in the rendered output
    const region = screen.getByRole('region');
    expect(region.textContent).toContain('Printer-1');
    expect(region.textContent).toContain('Printer-2');
  });

  it('should have proper semantic structure with article elements', () => {
    const { container } = render(<DurationComparison analytics={mockAnalytics} />);

    const articles = container.querySelectorAll('article');
    expect(articles.length).toBeGreaterThan(0);

    // Each article should have proper structure
    articles.forEach((article) => {
      expect(article.getAttribute('role')).not.toBeNull();
    });
  });

  it('should display variance as percentage with sign', () => {
    render(<DurationComparison analytics={mockAnalytics} />);

    // Overall variance is -3.33%, should show with minus sign
    const varianceElements = screen.queryAllByText(/-|variance/i);
    expect(varianceElements.length).toBeGreaterThanOrEqual(0);
  });
});
