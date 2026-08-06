import '@testing-library/jest-dom';
import React from 'react';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

const hoisted = vi.hoisted(() => ({
  getPrinters: vi.fn().mockResolvedValue([]),
  hasPermission: vi.fn(() => true),
}));

vi.mock('@/services/api', () => ({
  apiClient: { getPrinters: hoisted.getPrinters },
}));

vi.mock('@/services/printerHubService', () => ({
  printerHubService: {
    start: vi.fn().mockResolvedValue(undefined),
    stop: vi.fn().mockResolvedValue(undefined),
    onImportProgress: vi.fn(() => () => {}),
    onImportComplete: vi.fn(() => () => {}),
  },
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission: hoisted.hasPermission }),
}));

import PrinterImportExportControls from '../PrinterImportExportControls';

function renderWithClient() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <PrinterImportExportControls />
    </QueryClientProvider>,
  );
}

describe('PrinterImportExportControls lazy-loaded ImportExportModal (#1146 item 10)', () => {
  beforeEach(() => {
    hoisted.getPrinters.mockClear();
    hoisted.hasPermission.mockReturnValue(true);
  });

  it('does not mount the modal (or its chunk) before the trigger is clicked', () => {
    renderWithClient();

    expect(screen.queryByText('Import / Export Printers')).not.toBeInTheDocument();
  });

  it('lazily resolves and renders the real ImportExportModal once opened (Suspense round-trip)', async () => {
    const user = userEvent.setup();
    renderWithClient();

    await user.click(screen.getByRole('button', { name: 'Import / Export' }));

    // The real component (not a stub) renders once the dynamic import
    // resolves — proves the `lazyWithPreload` + `Suspense` wiring works,
    // not just that *some* placeholder appeared.
    expect(await screen.findByText('Import / Export Printers')).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Import' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Export' })).toBeInTheDocument();
  });

  it('exposes a working .preload() that can be invoked from hover/focus without throwing', async () => {
    const user = userEvent.setup();
    renderWithClient();

    const trigger = screen.getByRole('button', { name: 'Import / Export' });
    await user.hover(trigger);
    await user.unhover(trigger);

    // Preloading must not itself open the modal or throw synchronously.
    expect(screen.queryByText('Import / Export Printers')).not.toBeInTheDocument();

    // Opening afterwards still works normally (preload just warmed the cache).
    await user.click(trigger);
    expect(await screen.findByText('Import / Export Printers')).toBeInTheDocument();
  });

  it('renders nothing at all for non-admin users (permission gate unaffected by lazy-loading)', () => {
    hoisted.hasPermission.mockReturnValue(false);
    const { container } = renderWithClient();

    expect(container).toBeEmptyDOMElement();
  });
});