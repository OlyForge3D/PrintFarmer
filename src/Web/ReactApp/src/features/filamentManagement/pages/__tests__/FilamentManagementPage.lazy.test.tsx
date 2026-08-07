import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const moduleLoads = vi.hoisted(() => ({
  filaments: vi.fn(),
  spools: vi.fn(),
  clusters: vi.fn(),
  scanner: vi.fn(),
}));
const scannerModule = vi.hoisted(() => {
  let resolve: () => void = () => undefined;
  const ready = new Promise<void>((resolveReady) => {
    resolve = resolveReady;
  });
  return { ready, resolve };
});

vi.mock('@/features/filamentManagement/components/FilamentsTab', () => {
  moduleLoads.filaments();
  return { FilamentsTab: () => <div data-testid="filaments-content">Filaments content</div> };
});

vi.mock('@/features/filamentManagement/components/SpoolsTab', () => {
  moduleLoads.spools();
  return { SpoolsTab: () => <div data-testid="spools-content">Spools content</div> };
});

vi.mock('@/features/filamentManagement/components/MaterialClustersTab', () => {
  moduleLoads.clusters();
  return { MaterialClustersTab: () => <div data-testid="clusters-content">Clusters content</div> };
});

vi.mock('@/features/filamentManagement/components/ScanSpoolModal', async () => {
  moduleLoads.scanner();
  await scannerModule.ready;
  return {
    ScanSpoolModal: ({ isOpen }: { isOpen: boolean }) => (
      isOpen ? <div role="dialog" aria-label="Scan spool mock">Scanner content</div> : null
    ),
  };
});

import { FilamentManagementPage } from '../FilamentManagementPage';

describe('FilamentManagementPage lazy boundaries', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.clearAllMocks();
  });

  it('loads each tab and the scanner only when focused or activated', async () => {
    const user = userEvent.setup();
    render(
      <MemoryRouter initialEntries={['/spools/filaments']}>
        <Routes>
          <Route path="/spools/:tabId" element={<FilamentManagementPage />} />
        </Routes>
      </MemoryRouter>,
    );

    expect(await screen.findByTestId('filaments-content')).toBeInTheDocument();
    expect(moduleLoads.filaments).toHaveBeenCalledTimes(1);
    expect(moduleLoads.spools).not.toHaveBeenCalled();
    expect(moduleLoads.clusters).not.toHaveBeenCalled();
    expect(moduleLoads.scanner).not.toHaveBeenCalled();

    const spoolsTab = screen.getByRole('tab', { name: 'Spools' });
    fireEvent.focus(spoolsTab);
    await waitFor(() => expect(moduleLoads.spools).toHaveBeenCalledTimes(1));
    expect(screen.queryByTestId('spools-content')).not.toBeInTheDocument();
    await user.click(spoolsTab);
    expect(await screen.findByTestId('spools-content')).toBeInTheDocument();

    const clustersTab = screen.getByRole('tab', { name: 'Material Clusters' });
    fireEvent.focus(clustersTab);
    await waitFor(() => expect(moduleLoads.clusters).toHaveBeenCalledTimes(1));
    expect(screen.queryByTestId('clusters-content')).not.toBeInTheDocument();
    await user.click(clustersTab);
    expect(await screen.findByTestId('clusters-content')).toBeInTheDocument();

    const scanButton = screen.getByRole('button', { name: 'Scan' });
    fireEvent.click(scanButton);
    expect(await screen.findByRole('status', { name: 'Loading spool scanner' })).toBeVisible();
    await waitFor(() => expect(moduleLoads.scanner).toHaveBeenCalledTimes(1));
    scannerModule.resolve();
    expect(await screen.findByRole('dialog', { name: 'Scan spool mock' })).toBeInTheDocument();
  });
});
