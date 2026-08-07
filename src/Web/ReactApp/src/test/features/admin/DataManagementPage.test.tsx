import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { DataManagementPage } from '@/features/admin/pages/DataManagementPage';
import { exportCatalog } from '@/services/adminDataService';

vi.mock('@/services/adminDataService', () => ({
  exportCatalog: vi.fn(),
  exportPrinters: vi.fn(),
  exportFull: vi.fn(),
  importCatalog: vi.fn(),
  importFull: vi.fn(),
  reloadSeed: vi.fn(),
  downloadAsJson: vi.fn(),
  generateExportFilename: vi.fn(() => 'catalog.json'),
  getCatalogVersion: vi.fn(),
  checkCatalogUpdates: vi.fn(),
  applyCatalogUpdates: vi.fn(),
}));

describe('DataManagementPage shared admin patterns', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders operation failures through AdminError', async () => {
    vi.mocked(exportCatalog).mockRejectedValue(new Error('download unavailable'));
    const user = userEvent.setup();
    render(<DataManagementPage />);

    await user.click(screen.getByRole('button', { name: 'Export Catalog' }));

    const error = await screen.findByRole('alert');
    expect(error).toHaveTextContent('Data operation failed');
    expect(error).toHaveTextContent('Failed to export catalog: download unavailable');
  });
});
