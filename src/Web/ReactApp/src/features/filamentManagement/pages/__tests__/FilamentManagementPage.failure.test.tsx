import React from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('@/features/filamentManagement/components/FilamentsTab', () => ({
  FilamentsTab: () => <div>Filaments content</div>,
}));

vi.mock('@/features/filamentManagement/components/SpoolsTab', () => {
  throw new Error('Spools chunk failed');
});

vi.mock('@/features/filamentManagement/components/MaterialClustersTab', () => ({
  MaterialClustersTab: () => <div>Clusters content</div>,
}));

vi.mock('@/features/filamentManagement/components/ScanSpoolModal', () => ({
  ScanSpoolModal: () => null,
}));

import { FilamentManagementPage } from '../FilamentManagementPage';

describe('FilamentManagementPage lazy failure recovery', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
  });

  it('clears a failed tab boundary when the user switches to a healthy tab', async () => {
    render(
      <MemoryRouter initialEntries={['/spools/filaments']}>
        <Routes>
          <Route path="/spools/:tabId" element={<FilamentManagementPage />} />
        </Routes>
      </MemoryRouter>,
    );

    expect(await screen.findByText('Filaments content')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('tab', { name: 'Spools' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Unable to load spools.');

    fireEvent.click(screen.getByRole('tab', { name: 'Material Clusters' }));
    expect(await screen.findByText('Clusters content')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
