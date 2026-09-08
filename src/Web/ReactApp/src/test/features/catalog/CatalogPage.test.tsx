import '@testing-library/jest-dom';
import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { CatalogPage } from '@/features/catalog/pages/CatalogPage';

const mockUseAdminHubParent = vi.fn();

vi.mock('@/features/admin/utils/adminHubParentState', () => ({
  useAdminHubParent: () => mockUseAdminHubParent(),
}));

vi.mock('@/features/catalog/components/PrinterModelsCatalog', () => ({
  PrinterModelsCatalog: () => <div>Printer catalog</div>,
}));

vi.mock('@/features/catalog/components/HotendsCatalog', () => ({
  HotendsCatalog: () => <div>Hotends catalog</div>,
}));

vi.mock('@/features/catalog/components/ExtrudersCatalog', () => ({
  ExtrudersCatalog: () => <div>Extruders catalog</div>,
}));

vi.mock('@/features/catalog/components/ToolheadsCatalog', () => ({
  ToolheadsCatalog: () => <div>Toolheads catalog</div>,
}));

vi.mock('@/features/catalog/components/NozzlesCatalog', () => ({
  NozzlesCatalog: () => <div>Nozzles catalog</div>,
}));

vi.mock('@/features/catalog/components/FilamentsCatalog', () => ({
  FilamentsCatalog: () => <div>Filaments catalog</div>,
}));

function renderPage(initialEntry = '/catalog') {
  return render(
    <MemoryRouter initialEntries={[initialEntry]}>
      <CatalogPage />
    </MemoryRouter>,
  );
}

describe('CatalogPage', () => {
  it('shows the Admin Control Center parent when entered from an admin-origin route', () => {
    mockUseAdminHubParent.mockReturnValue({ label: 'Admin Control Center', to: '/admin' });
    renderPage();

    expect(screen.getByRole('link', { name: 'Admin Control Center' })).toHaveAttribute('href', '/admin');
  });

  it('does not show the Admin Control Center parent during ordinary catalog navigation', () => {
    mockUseAdminHubParent.mockReturnValue(undefined);
    renderPage();

    expect(screen.queryByRole('link', { name: 'Admin Control Center' })).not.toBeInTheDocument();
  });
});
