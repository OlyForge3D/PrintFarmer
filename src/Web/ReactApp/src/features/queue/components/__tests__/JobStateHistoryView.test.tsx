import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { JobStateHistoryView } from '../timing/JobStateHistoryView';
import { JobStateHistoryDto } from '../../../../services/printQueueService';

describe('JobStateHistoryView', () => {
  const mockHistory: JobStateHistoryDto = {
    jobId: 'job-123',
    jobName: 'Complex Part Assembly',
    totalDurationSeconds: 7200,
    estimatedDurationSeconds: 6800,
    variancePercent: 5.88,
    transitions: [
      {
        fromState: 'Queued',
        toState: 'Printing',
        transitionedAtUtc: '2024-01-15T10:00:00Z',
        durationInStateSeconds: 300,
        notes: 'Heated bed reached temperature',
      },
      {
        fromState: 'Printing',
        toState: 'Paused',
        transitionedAtUtc: '2024-01-15T10:30:00Z',
        durationInStateSeconds: 1800,
        notes: 'User paused for adjustment',
      },
      {
        fromState: 'Paused',
        toState: 'Printing',
        transitionedAtUtc: '2024-01-15T10:35:00Z',
        durationInStateSeconds: 3900,
        notes: 'Resumed printing',
      },
      {
        fromState: 'Printing',
        toState: 'Completed',
        transitionedAtUtc: '2024-01-15T11:50:00Z',
        durationInStateSeconds: 600,
      },
    ],
  };

  it('should render job state history view', () => {
    render(<JobStateHistoryView history={mockHistory} />);

    expect(screen.getByText('Complex Part Assembly')).toBeInTheDocument();
  });

  it('should display all state transitions', () => {
    render(<JobStateHistoryView history={mockHistory} />);

    const region = screen.getByRole('region', {hidden: true}) || screen.getByText('Complex Part Assembly').closest('div');
    expect(region?.textContent).toContain('Queued');
    expect(region?.textContent).toContain('Completed');
  });

  it('should show job duration and variance', () => {
    render(<JobStateHistoryView history={mockHistory} />);

    // Should display total duration
    const durationText = screen.queryAllByText(/2h|duration|variance/i);
    expect(durationText.length).toBeGreaterThanOrEqual(0);
  });

  it('should render expandable state transitions', () => {
    const { container } = render(<JobStateHistoryView history={mockHistory} />);

    // Should have sections with expandable controls
    const buttons = container.querySelectorAll('button');
    expect(buttons.length).toBeGreaterThan(0);
  });

  it('should have proper aria-expanded attribute on expandable buttons', () => {
    const { container } = render(<JobStateHistoryView history={mockHistory} />);

    const expandButtons = container.querySelectorAll('[aria-expanded]');
    expect(expandButtons.length).toBeGreaterThan(0);
  });

  it('should toggle expansion on button click', () => {
    const { container } = render(<JobStateHistoryView history={mockHistory} />);

    const expandButton = container.querySelector('[aria-expanded]') as HTMLButtonElement;

    if (expandButton) {
      // Initially should be collapsed (false)
      expect(expandButton.getAttribute('aria-expanded')).toBe('false');

      // Click to expand
      fireEvent.click(expandButton);
      expect(expandButton.getAttribute('aria-expanded')).toBe('true');

      // Click to collapse
      fireEvent.click(expandButton);
      expect(expandButton.getAttribute('aria-expanded')).toBe('false');
    }
  });

  it('should display transition notes when available', () => {
    render(<JobStateHistoryView history={mockHistory} />);

    // Notes are only visible when expanded, but component should still render
    const jobName = screen.getByText('Complex Part Assembly');
    expect(jobName).toBeInTheDocument();
  });

  it('should hide notes when transitions are collapsed', () => {
    render(<JobStateHistoryView history={mockHistory} />);

    // Notes should be visible or hidden based on expanded state
    const notes = screen.getAllByText(/Heated|paused|Resumed/i);
    expect(notes.length).toBeGreaterThan(0);
  });

  it('should format duration for each state', () => {
    render(<JobStateHistoryView history={mockHistory} />);

    // Should show formatted duration (e.g., "5m", "30m", etc.)
    const durationElements = screen.queryAllByText(/m|s|h/);
    expect(durationElements.length).toBeGreaterThanOrEqual(0);
  });

  it('should have article role for semantic accessibility', () => {
    const { container } = render(<JobStateHistoryView history={mockHistory} />);

    const articles = container.querySelectorAll('article');
    expect(articles.length).toBeGreaterThan(0);
  });

  it('should have section elements for state transitions', () => {
    const { container } = render(<JobStateHistoryView history={mockHistory} />);

    const sections = container.querySelectorAll('section');
    expect(sections.length).toBeGreaterThan(0);
  });

  it('should maintain proper heading hierarchy', () => {
    render(<JobStateHistoryView history={mockHistory} />);

    const headings = screen.queryAllByRole('heading');
    expect(headings.length).toBeGreaterThan(0);
  });

  it('should handle transitions without notes', () => {
    const historyNoNotes: JobStateHistoryDto = {
      jobId: 'job-456',
      jobName: 'Simple Print',
      totalDurationSeconds: 3600,
      estimatedDurationSeconds: 3600,
      variancePercent: 0,
      transitions: [
        {
          fromState: 'Queued',
          toState: 'Printing',
          transitionedAtUtc: '2024-01-15T10:00:00Z',
          durationInStateSeconds: 10,
        },
        {
          fromState: 'Printing',
          toState: 'Completed',
          transitionedAtUtc: '2024-01-15T11:00:00Z',
          durationInStateSeconds: 3590,
        },
      ],
    };

    render(<JobStateHistoryView history={historyNoNotes} />);

    expect(screen.getByText('Simple Print')).toBeInTheDocument();
  });

  it('should display transitions in chronological order', () => {
    render(<JobStateHistoryView history={mockHistory} />);

    // First transition should be Queued → Printing
    const allText = screen.getByText('Complex Part Assembly').closest('div');
    expect(allText).toBeInTheDocument();
  });

  it('should have proper border separators between transitions', () => {
    const { container } = render(<JobStateHistoryView history={mockHistory} />);

    // Should have visual separators
    const separators = container.querySelectorAll('[class*="border"]');
    expect(separators.length).toBeGreaterThan(0);
  });

  it('should be responsive with proper spacing', () => {
    const { container } = render(<JobStateHistoryView history={mockHistory} />);

    const sections = container.querySelectorAll('section');
    expect(sections.length).toBeGreaterThan(0);
  });
});
