import React from 'react';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { LazyContentBoundary } from '@/common/components/LazyContentBoundary';
import { lazyWithPreload } from '@/common/utils/lazyWithPreload';

describe('LazyContentBoundary', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('retries a rejected content chunk without unmounting surrounding route state', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const user = userEvent.setup();
    const LoadedContent = () => <div>Loaded workspace</div>;
    const factory = vi.fn()
      .mockRejectedValueOnce(new Error('Chunk request failed'))
      .mockResolvedValueOnce({ default: LoadedContent });
    const RetryableContent = lazyWithPreload<Record<string, never>, React.FC>(factory);

    render(
      <div data-testid="route-state">
        Unsaved route state
        <LazyContentBoundary
          label="workspace"
          fallback={<div role="status">Loading workspace</div>}
          onRetry={RetryableContent.retry}
        >
          <RetryableContent />
        </LazyContentBoundary>
      </div>,
    );

    expect(await screen.findByRole('alert')).toHaveTextContent('Unable to load workspace.');
    expect(screen.getByTestId('route-state')).toHaveTextContent('Unsaved route state');

    await user.click(screen.getByRole('button', { name: 'Retry' }));

    expect(await screen.findByText('Loaded workspace')).toBeInTheDocument();
    expect(screen.getByTestId('route-state')).toHaveTextContent('Unsaved route state');
    expect(factory).toHaveBeenCalledTimes(2);
  });
});
