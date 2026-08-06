import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import HistoryFiltersBar from '../HistoryFiltersBar';
import ModelFiltersBar from '../ModelFiltersBar';

const expectNonColorHoverFeedback = (button: HTMLElement) => {
  expect(button).toHaveClass(
    'enabled:hover:scale-105',
    'enabled:hover:shadow-sm',
  );
  expect(button).not.toHaveClass(
    'hover:bg-pf-success',
    'hover:bg-pf-error',
    'hover:bg-pf-warning',
  );
};

describe('queue status filter hover feedback', () => {
  it('uses non-color hover feedback for selected history statuses', () => {
    render(
      <HistoryFiltersBar
        selectedStatuses={['completed', 'failed', 'cancelled']}
        onStatusChange={vi.fn()}
        sortBy="newest"
        onSortChange={vi.fn()}
        onRefresh={vi.fn().mockResolvedValue(undefined)}
        isLoading={false}
        viewMode="cards"
        onViewModeChange={vi.fn()}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /Filters/ }));

    expectNonColorHoverFeedback(screen.getByRole('button', { name: /Done/ }));
    expectNonColorHoverFeedback(screen.getByRole('button', { name: /Failed/ }));
    expectNonColorHoverFeedback(screen.getByRole('button', { name: /Cancelled/ }));
  });

  it('uses non-color hover feedback for selected model statuses', () => {
    render(
      <ModelFiltersBar
        models={[]}
        selectedModel={null}
        onModelChange={vi.fn()}
        selectedStatuses={['queued', 'printing', 'paused']}
        onStatusChange={vi.fn()}
        sortBy="name"
        onSortChange={vi.fn()}
        onRefresh={vi.fn()}
        isLoading={false}
      />,
    );

    expectNonColorHoverFeedback(screen.getByRole('button', { name: /Queued/ }));
    expectNonColorHoverFeedback(screen.getByRole('button', { name: /Printing/ }));
    expectNonColorHoverFeedback(screen.getByRole('button', { name: /Paused/ }));
  });
});
