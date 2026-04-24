import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, act } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

vi.mock('@/common/components/ui', () => ({
  Badge: ({ children }: { children: React.ReactNode }) => (
    <span data-testid="badge">{children}</span>
  ),
}));

import {
  EstimatedCompletionBadge,
} from '../components/EstimatedCompletionBadge';
import {
  formatTimeRemaining,
  formatCompletionTime,
} from '../utils/completionTime';

describe('formatTimeRemaining', () => {
  it('returns "Done soon" when diff is zero or negative', () => {
    expect(formatTimeRemaining(0)).toBe('Done soon');
    expect(formatTimeRemaining(-5000)).toBe('Done soon');
  });

  it('shows minutes for < 1 hour', () => {
    expect(formatTimeRemaining(15 * 60_000)).toBe('~15m left');
    expect(formatTimeRemaining(45 * 60_000)).toBe('~45m left');
  });

  it('shows hours and minutes for < 24 hours', () => {
    expect(formatTimeRemaining(2 * 3600_000 + 15 * 60_000)).toBe('~2h 15m left');
  });

  it('shows hours only when minutes are 0', () => {
    expect(formatTimeRemaining(3 * 3600_000)).toBe('~3h left');
  });

  it('shows days and hours for >= 24 hours', () => {
    expect(formatTimeRemaining(26 * 3600_000)).toBe('~1d 2h left');
  });

  it('shows days only when remaining hours are 0', () => {
    expect(formatTimeRemaining(48 * 3600_000)).toBe('~2d left');
  });
});

describe('formatCompletionTime', () => {
  it('returns time only for same-day completion', () => {
    // "now" is Jan 15, 2025 10:00 AM
    const now = new Date(2025, 0, 15, 10, 0, 0).getTime();
    // completion is Jan 15, 2025 3:45 PM
    const completion = new Date(2025, 0, 15, 15, 45, 0).getTime();
    const result = formatCompletionTime(completion, now);
    // Should contain the time but NOT "Tomorrow" or a date
    expect(result).not.toContain('Tomorrow');
    expect(result).toMatch(/3:45/);
  });

  it('returns "Tomorrow <time>" for next-day completion', () => {
    const now = new Date(2025, 0, 15, 22, 0, 0).getTime();
    const completion = new Date(2025, 0, 16, 3, 30, 0).getTime();
    const result = formatCompletionTime(completion, now);
    expect(result).toContain('Tomorrow');
    expect(result).toMatch(/3:30/);
  });

  it('returns date and time for completion 2+ days out', () => {
    const now = new Date(2025, 0, 15, 10, 0, 0).getTime();
    const completion = new Date(2025, 3, 25, 15, 45, 0).getTime();
    const result = formatCompletionTime(completion, now);
    expect(result).toContain('Apr');
    expect(result).toContain('25');
    expect(result).toMatch(/3:45/);
  });
});

describe('EstimatedCompletionBadge', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('renders countdown and absolute time with printTimeLeftSeconds', () => {
    // Set "now" to Jan 15, 2025 10:00 AM
    vi.setSystemTime(new Date(2025, 0, 15, 10, 0, 0));

    render(<EstimatedCompletionBadge printTimeLeftSeconds={7200} />);

    // Badge should show countdown (~2h left)
    expect(screen.getByTestId('badge')).toHaveTextContent('~2h left');
    // Absolute time should appear — completion is same day (noon)
    expect(screen.getByText(/done ~/)).toBeInTheDocument();
  });

  it('renders with completionTimeUtc when printTimeLeftSeconds is absent', () => {
    vi.setSystemTime(new Date(2025, 0, 15, 10, 0, 0));
    const completionUtc = new Date(2025, 0, 15, 14, 30, 0).toISOString();

    render(<EstimatedCompletionBadge completionTimeUtc={completionUtc} />);

    expect(screen.getByTestId('badge')).toHaveTextContent('left');
    expect(screen.getByText(/done ~/)).toBeInTheDocument();
  });

  it('renders nothing when no ETA data is provided', () => {
    const { container } = render(<EstimatedCompletionBadge />);
    expect(container.firstChild).toBeNull();
  });

  it('updates on 30-second tick interval', () => {
    vi.setSystemTime(new Date(2025, 0, 15, 10, 0, 0));

    render(<EstimatedCompletionBadge printTimeLeftSeconds={3600} />);
    expect(screen.getByTestId('badge')).toHaveTextContent('~1h left');

    // Advance 30 seconds — tick fires, countdown recalculates
    act(() => {
      vi.advanceTimersByTime(30_000);
    });

    // Badge should still render (59m 30s rounds to ~60m = ~1h left)
    expect(screen.getByTestId('badge')).toBeInTheDocument();
  });

  it('shows "Tomorrow" for next-day completion', () => {
    // Set "now" to 11 PM — 2 hours of print left crosses midnight
    vi.setSystemTime(new Date(2025, 0, 15, 23, 0, 0));

    render(<EstimatedCompletionBadge printTimeLeftSeconds={7200} />);

    expect(screen.getByText(/Tomorrow/)).toBeInTheDocument();
  });
});
