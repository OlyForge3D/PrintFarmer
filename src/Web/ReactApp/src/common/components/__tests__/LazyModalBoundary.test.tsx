import React, { useState } from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { LazyModalBoundary } from '@/common/components/LazyModalFallback';
import { lazyWithPreload } from '@/common/utils/lazyWithPreload';

describe('LazyModalBoundary', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('contains focus, dismisses on Escape, and restores trigger focus while loading', async () => {
    const user = userEvent.setup();
    const PendingModal = lazyWithPreload<Record<string, never>, React.FC>(
      () => new Promise(() => undefined),
    );

    function Harness() {
      const [isOpen, setIsOpen] = useState(false);
      return (
        <>
          <button type="button" onClick={() => setIsOpen(true)}>Open modal</button>
          {isOpen && (
            <LazyModalBoundary
              label="test modal"
              onCancel={() => setIsOpen(false)}
              onRetry={PendingModal.retry}
            >
              <PendingModal />
            </LazyModalBoundary>
          )}
        </>
      );
    }

    render(<Harness />);
    const trigger = screen.getByRole('button', { name: 'Open modal' });
    await user.click(trigger);

    const loadingDialog = await screen.findByRole('dialog', { name: 'Loading test modal' });
    const cancelButton = screen.getByRole('button', { name: 'Cancel' });
    expect(loadingDialog).toContainElement(cancelButton);
    expect(cancelButton).toHaveFocus();

    await user.tab();
    expect(cancelButton).toHaveFocus();

    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog', { name: 'Loading test modal' })).not.toBeInTheDocument();
    await waitFor(() => expect(trigger).toHaveFocus());
  });

  it('recreates a rejected lazy payload and retries without unmounting the route', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const user = userEvent.setup();
    const LoadedModal = () => <div role="dialog" aria-label="Recovered modal">Recovered</div>;
    const factory = vi.fn()
      .mockRejectedValueOnce(new Error('Chunk request failed'))
      .mockResolvedValueOnce({ default: LoadedModal });
    const RetryableModal = lazyWithPreload<Record<string, never>, React.FC>(factory);

    render(
      <div data-testid="route-state">
        Unsaved route state
        <LazyModalBoundary
          label="test modal"
          onCancel={vi.fn()}
          onRetry={RetryableModal.retry}
        >
          <RetryableModal />
        </LazyModalBoundary>
      </div>,
    );

    expect(await screen.findByRole('alert')).toHaveTextContent('Unable to load test modal.');
    expect(screen.getByTestId('route-state')).toHaveTextContent('Unsaved route state');

    await user.click(screen.getByRole('button', { name: 'Retry' }));

    expect(await screen.findByRole('dialog', { name: 'Recovered modal' })).toBeInTheDocument();
    expect(screen.getByTestId('route-state')).toHaveTextContent('Unsaved route state');
    expect(factory).toHaveBeenCalledTimes(2);
  });
});
