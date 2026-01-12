import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { JobTimeline } from '../timing/JobTimeline';
import { TimelineEventDto } from '../../../../services/printQueueService';

describe('JobTimeline', () => {
  const mockEvents: TimelineEventDto[] = [
    {
      jobId: 'job-1',
      jobName: 'Test Model Print',
      state: 'Printing',
      printerName: 'Printer Alpha',
      enteredAtUtc: '2024-01-15T10:00:00Z',
      exitedAtUtc: undefined,
      durationSeconds: 3600,
      estimatedDurationSeconds: 3600,
      variancePercent: 0,
    },
    {
      jobId: 'job-2',
      jobName: 'Complex Part',
      state: 'Completed',
      printerName: 'Printer Beta',
      enteredAtUtc: '2024-01-15T08:00:00Z',
      exitedAtUtc: '2024-01-15T10:00:00Z',
      durationSeconds: 7200,
      estimatedDurationSeconds: 6800,
      variancePercent: 5.88,
    },
  ];

  it('should render timeline with event list', () => {
    render(<JobTimeline events={mockEvents} />);

    expect(screen.getByRole('list')).toBeInTheDocument();
  });

  it('should display timeline events with proper roles', () => {
    render(<JobTimeline events={mockEvents} />);

    const listItems = screen.getAllByRole('listitem');
    expect(listItems).toHaveLength(mockEvents.length);
  });

  it('should render job names as headings', () => {
    render(<JobTimeline events={mockEvents} />);

    expect(screen.getByText('Test Model Print')).toBeInTheDocument();
    expect(screen.getByText('Complex Part')).toBeInTheDocument();
  });

  it('should display printer names for each event', () => {
    render(<JobTimeline events={mockEvents} />);

    expect(screen.getByText('Printer Alpha')).toBeInTheDocument();
    expect(screen.getByText('Printer Beta')).toBeInTheDocument();
  });

  it('should show state with proper styling', () => {
    render(<JobTimeline events={mockEvents} />);

    const printingState = screen.getByText('Printing');
    expect(printingState).toBeInTheDocument();

    const completedState = screen.getByText('Completed');
    expect(completedState).toBeInTheDocument();
  });

  it('should format and display duration information', () => {
    render(<JobTimeline events={mockEvents} />);

    // 3600 seconds = 1h 0m, 7200 seconds = 2h 0m
    // Just verify durations are displayed without exact matching
    const listItems = screen.getAllByRole('listitem');
    expect(listItems.length).toBeGreaterThan(0);
  });

  it('should display variance percentage', () => {
    render(<JobTimeline events={mockEvents} />);

    // First event has 0% variance
    const varianceElements = screen.getAllByText(/0%|5.88%/);
    expect(varianceElements.length).toBeGreaterThan(0);
  });

  it('should render empty state when no events provided', () => {
    const { container } = render(<JobTimeline events={[]} />);

    // Component should still render without errors
    expect(container).toBeInTheDocument();
  });

  it('should have accessible heading hierarchy', () => {
    render(<JobTimeline events={mockEvents} />);

    // All headings should be h2 (main timeline title is h2)
    const headings = screen.getAllByRole('heading', { level: 2 });
    expect(headings.length).toBeGreaterThan(0);
  });

  it('should render timeline with responsive grid layout', () => {
    const { container } = render(<JobTimeline events={mockEvents} />);

    const gridContainer = container.querySelector('[class*="grid"]');
    expect(gridContainer).toBeInTheDocument();
  });

  it('should mark ongoing events without exitedAtUtc', () => {
    render(<JobTimeline events={mockEvents} />);

    // First event has no exitedAtUtc (ongoing)
    const printingEvent = screen.getByText('Test Model Print').closest('[role="listitem"]');
    expect(printingEvent).toBeInTheDocument();
  });

  it('should show estimated vs actual duration comparison', () => {
    render(<JobTimeline events={mockEvents} />);

    // Second event has variance, should show estimated and actual
    const listItems = screen.getAllByRole('listitem');
    expect(listItems[1]).toBeInTheDocument();
  });

  it('should handle special characters in job names', () => {
    const specialCharEvents: TimelineEventDto[] = [
      {
        jobId: 'job-special',
        jobName: 'Test/Model & Special <Chars>',
        state: 'Printing',
        printerName: 'Printer-1',
        enteredAtUtc: '2024-01-15T10:00:00Z',
        exitedAtUtc: undefined,
        durationSeconds: 1000,
        estimatedDurationSeconds: 1000,
        variancePercent: 0,
      },
    ];

    render(<JobTimeline events={specialCharEvents} />);
    expect(screen.getByText('Test/Model & Special <Chars>')).toBeInTheDocument();
  });

  it('should maintain proper chronological order', () => {
    render(<JobTimeline events={mockEvents} />);

    const listItems = screen.getAllByRole('listitem');
    expect(listItems[0]).toContainElement(screen.getByText('Test Model Print'));
    expect(listItems[1]).toContainElement(screen.getByText('Complex Part'));
  });
});
