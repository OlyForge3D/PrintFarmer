import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { HarvestJobAction } from '../HarvestJobAction';
import { configurePartsHarvestClient } from '@/services/partsHarvest';

function wrap(children: ReactNode) {
  const qc = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
  return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
}

describe('HarvestJobAction', () => {
  beforeEach(() => {
    configurePartsHarvestClient({
      get: vi.fn().mockResolvedValue({ data: [] }),
      post: vi.fn().mockResolvedValue({ data: {} }),
    });
  });

  afterEach(() => {
    configurePartsHarvestClient(null);
  });

  it('renders a Harvest button when the job is not yet harvested', () => {
    render(
      wrap(
        <HarvestJobAction job={{ id: 'j1', name: 'Job A' }} variant="table" />,
      ),
    );
    const button = screen.getByTestId('harvest-button');
    expect(button).toBeInTheDocument();
    expect(button).toHaveAttribute('aria-label', expect.stringContaining('Job A'));
    expect(screen.queryByTestId('harvest-badge')).not.toBeInTheDocument();
  });

  it('renders a harvested badge (as a button) when harvestedAt is set', () => {
    render(
      wrap(
        <HarvestJobAction
          job={{ id: 'j1', name: 'Job B', harvestedAt: '2026-01-01T00:00:00Z' }}
          variant="table"
        />,
      ),
    );
    const badge = screen.getByTestId('harvest-badge');
    expect(badge).toBeInTheDocument();
    expect(badge.tagName).toBe('BUTTON');
    expect(screen.queryByTestId('harvest-button')).not.toBeInTheDocument();
  });

  it('opens the dialog when the button is clicked', async () => {
    const user = userEvent.setup();
    render(
      wrap(
        <HarvestJobAction job={{ id: 'j1', name: 'Job A' }} variant="table" />,
      ),
    );
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    await user.click(screen.getByTestId('harvest-button'));
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('opens the dialog in already-harvested mode when the badge is clicked', async () => {
    const user = userEvent.setup();
    render(
      wrap(
        <HarvestJobAction
          job={{ id: 'j1', name: 'Job C', harvestedAt: '2026-01-01T00:00:00Z' }}
          variant="table"
        />,
      ),
    );
    await user.click(screen.getByTestId('harvest-badge'));
    expect(screen.getByTestId('harvest-already-harvested')).toBeInTheDocument();
  });

  it('uses a full-width variant when card layout is requested', () => {
    render(
      wrap(
        <HarvestJobAction job={{ id: 'j1', name: 'Job A' }} variant="card" />,
      ),
    );
    const button = screen.getByTestId('harvest-button');
    expect(button.className).toMatch(/flex-1/);
  });
});
