import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { describe, it, expect, vi, beforeEach } from 'vitest';

vi.mock('../components/PartsTab', () => ({
  PartsTab: () => <div data-testid="tab-skus">SKUs tab</div>,
}));
vi.mock('../components/BinsTab', () => ({
  BinsTab: () => <div data-testid="tab-bins">Bins tab</div>,
}));
vi.mock('../components/MappingsTab', () => ({
  MappingsTab: () => <div data-testid="tab-mappings">Mappings tab</div>,
}));
vi.mock('../components/ReorderTab', () => ({
  ReorderTab: () => <div data-testid="tab-reorder">Reorder tab</div>,
}));

vi.mock('../hooks/usePartsInventory', () => ({
  useReorderCandidates: () => ({ data: [] }),
}));

vi.mock('@/common/hooks/useSystemCapabilities', () => ({
  useSystemCapabilities: () => ({
    data: { operatorFeatures: { printedPartsInventoryEnabled: true } },
    status: 'success',
  }),
}));

import { PartsInventoryPage } from '../pages/PartsInventoryPage';

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/parts-inventory" element={<PartsInventoryPage />} />
        <Route path="/parts-inventory/:tabId" element={<PartsInventoryPage />} />
      </Routes>
    </MemoryRouter>
  );
}

describe('PartsInventoryPage', () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  it('renders SKUs tab by default when URL has no tab', () => {
    renderAt('/parts-inventory/skus');
    expect(screen.getByTestId('tab-skus')).toBeInTheDocument();
  });

  it('renders Bins tab when URL says bins', () => {
    renderAt('/parts-inventory/bins');
    expect(screen.getByTestId('tab-bins')).toBeInTheDocument();
  });

  it('renders Mappings tab when URL says mappings', () => {
    renderAt('/parts-inventory/mappings');
    expect(screen.getByTestId('tab-mappings')).toBeInTheDocument();
  });

  it('renders Reorder tab when URL says reorder', () => {
    renderAt('/parts-inventory/reorder');
    expect(screen.getByTestId('tab-reorder')).toBeInTheDocument();
  });

  it('uses "Printed Parts" heading distinct from maintenance components', () => {
    renderAt('/parts-inventory/skus');
    expect(screen.getByRole('heading', { name: /Printed Parts/i })).toBeInTheDocument();
  });
});
