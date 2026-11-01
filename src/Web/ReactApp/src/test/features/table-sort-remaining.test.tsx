import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { CameraManagementPanel } from '@/features/cameras/components/CameraManagementPanel';
import { AccessControlTab } from '@/features/printer-groups/components/AccessControlTab';
import { OpenFilamentDbBrowserModal } from '@/features/filamentManagement/components/OpenFilamentDbBrowserModal';
import { ModelListView } from '@/features/models3d/components/ModelListView';
import { CameraHealthStatus, CameraSource, CameraType, PrinterGroupAccessLevel, type CameraDto, type OfdFlattenedEntry, type PrinterGroupAccessRule } from '@/types/api';
import type { Model } from '@/types/models';

const mocks = vi.hoisted(() => ({
  getCameras: vi.fn(),
  updateCamera: vi.fn(),
  getRules: vi.fn(),
  setRules: vi.fn(),
  importFilaments: vi.fn(),
}));
vi.mock('@/services/cameraService', () => ({
  cameraService: { getAllCameras: mocks.getCameras, toggleCamera: mocks.updateCamera },
}));
vi.mock('@/services/api', () => ({
  apiClient: {
    getPrinterGroupAccessRules: mocks.getRules,
    getRoles: vi.fn().mockResolvedValue([]),
    setPrinterGroupAccessRules: mocks.setRules,
  },
}));
vi.mock('@/features/cameras/components/EditCameraModal', () => ({ EditCameraModal: () => null }));
vi.mock('@/common/hooks/useApi', () => ({
  usePrinters: () => ({ data: [{ id: 'p-z', name: 'Zeta printer' }, { id: 'p-a', name: 'Alpha printer' }] }),
  useOfdBrands: () => ({ data: [{ id: 'b', slug: 'brand', name: 'Test brand', materialCount: 1 }], isLoading: false }),
  useOfdBrandMaterials: () => ({ data: { materials: [{ id: 'pla', slug: 'pla', material: 'PLA', filamentCount: 3 }] }, isLoading: false }),
  useOfdFilaments: () => ({ data: filaments, isLoading: false }),
  useImportFromOfd: () => ({ mutateAsync: mocks.importFilaments, isPending: false }),
}));

const camera = (id: string, overrides: Partial<CameraDto>): CameraDto => ({
  id, name: id, isEnabled: true, sortOrder: 0, isStandalone: true, createdAt: '2026-01-01',
  printerIds: [], printers: [], isLocationDerived: false, userModified: false,
  source: CameraSource.Standalone, cameraType: CameraType.General,
  healthStatus: CameraHealthStatus.Unknown, ...overrides,
});
const cameras = [
  camera('Zeta', { location: 'Workshop', printerId: 'p-z' }),
  camera('Alpha', { location: 'Attic', printerId: 'p-a', isEnabled: false }),
  camera('Missing', {}),
];
const rules: PrinterGroupAccessRule[] = [
  { id: 'z', roleId: 'rz', roleName: 'Zeta', accessLevel: PrinterGroupAccessLevel.View, createdDate: '2026-01-01' },
  { id: 'a', roleId: 'ra', roleName: 'Alpha', accessLevel: PrinterGroupAccessLevel.Manage, createdDate: '2026-01-01' },
  { id: 'm', roleId: 'rm', roleName: 'Middle', accessLevel: PrinterGroupAccessLevel.Submit, createdDate: '2026-01-01' },
];
const filament = (id: string, overrides: Partial<OfdFlattenedEntry>): OfdFlattenedEntry => ({
  entryId: id, filamentName: id, brandName: 'Test brand', material: 'PLA', colorName: '',
  weight: 10, diameter: 1.75, matte: false, translucent: false, glow: false, ...overrides,
});
const filaments = [
  filament('Zeta', { weight: 1000, diameter: 2.85, minPrintTemp: 220, maxPrintTemp: 250, minBedTemp: 100, colorName: 'Yellow', matte: true }),
  filament('Alpha', { weight: 2, diameter: 1.75, maxPrintTemp: 180, maxBedTemp: 20, colorName: 'Blue', glow: true }),
  filament('Missing', { weight: 10, diameter: 2, colorName: '' }),
];
const models: Model[] = [
  { id: 'z', path: 'z.stl', fileName: 'z.stl', name: 'Zeta', fileSize: 100, fileType: 'stl', uploadedAt: '2026-02-01', tags: [{ id: 'z', name: 'Zebra' }] },
  { id: 'a', path: 'a.3mf', fileName: 'a.3mf', name: 'Alpha', fileSize: 2, fileType: '3mf', uploadedAt: '2026-01-01', tags: [{ id: 'a', name: 'Apple' }] },
  { id: 'm', path: 'm.obj', fileName: 'm.obj', name: 'Missing', fileSize: 10, fileType: 'obj', uploadedAt: '2026-01-15' },
];
const defaultOrder = ['Zeta', 'Alpha', 'Missing'];
const order = (table: HTMLElement, nameCell = 0) => within(table).getAllByRole('row')
  .slice(1).map(row => within(row).getAllByRole('cell')[nameCell].textContent?.trim());
const provider = (children: React.ReactNode) => (
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    {children}
  </QueryClientProvider>
);
async function cycle(table: HTMLElement, label: string, ascending: string[], descending: string[], initial = defaultOrder, nameCell = 0) {
  const user = userEvent.setup();
  const header = within(table).getByRole('columnheader', { name: label });
  const button = within(header).getByRole('button', { name: label });
  expect(header).toHaveAttribute('aria-sort', 'none');
  await user.click(button);
  expect(header).toHaveAttribute('aria-sort', 'ascending');
  expect(order(table, nameCell)).toEqual(ascending);
  button.focus();
  await user.keyboard(' ');
  expect(header).toHaveAttribute('aria-sort', 'descending');
  expect(order(table, nameCell)).toEqual(descending);
  await user.keyboard('{Enter}');
  expect(header).toHaveAttribute('aria-sort', 'none');
  expect(order(table, nameCell)).toEqual(initial);
}

beforeEach(() => {
  vi.clearAllMocks();
  mocks.getCameras.mockResolvedValue(cameras);
  mocks.updateCamera.mockResolvedValue({});
  mocks.getRules.mockResolvedValue(rules);
  mocks.setRules.mockResolvedValue({});
  mocks.importFilaments.mockResolvedValue({ createdCount: 1, updatedCount: 0, errorCount: 0 });
});

describe('independent-row table sorting', () => {
  it.each(['Camera', 'Location', 'Printer'])('cycles camera %s and preserves the fetched order', async label => {
    render(<CameraManagementPanel />);
    await screen.findByText('Zeta');
    await cycle(screen.getByRole('table'), label, label === 'Camera' ? ['Alpha', 'Missing', 'Zeta'] : ['Alpha', 'Zeta', 'Missing'],
      label === 'Camera' ? ['Zeta', 'Missing', 'Alpha'] : ['Zeta', 'Alpha', 'Missing']);
    expect(cameras.map(item => item.name)).toEqual(defaultOrder);
  });

  it('sorts camera status and toggles the displayed camera, not its original row index', async () => {
    const user = userEvent.setup();
    render(<CameraManagementPanel />);
    await screen.findByText('Zeta');
    await cycle(screen.getByRole('table'), 'Status', ['Alpha', 'Zeta', 'Missing'], ['Zeta', 'Missing', 'Alpha']);
    await user.click(screen.getByRole('button', { name: 'Camera' }));
    await user.click(within(screen.getAllByRole('row')[1]).getByRole('button', { name: 'Enable camera' }));
    expect(mocks.updateCamera.mock.calls[0][0]).toBe('Alpha');
  });

  it.each(['Role', 'Access Level'])('cycles access %s without reordering the save payload', async label => {
    const user = userEvent.setup();
    render(provider(<AccessControlTab groupId="group" />));
    await screen.findByText('Zeta');
    await cycle(screen.getByRole('table'), label, ['Alpha', 'Middle', 'Zeta'], ['Zeta', 'Middle', 'Alpha'], ['Zeta', 'Alpha', 'Middle']);
    await user.click(screen.getByRole('button', { name: label }));
    await user.click(within(screen.getAllByRole('row')[1]).getByRole('button', { name: 'Remove' }));
    expect(mocks.setRules).toHaveBeenCalledWith('group', {
      rules: [{ roleId: 'rz', accessLevel: 'View' }, { roleId: 'rm', accessLevel: 'Submit' }],
    });
    expect(rules.map(rule => rule.id)).toEqual(['z', 'a', 'm']);
  });

  it.each([
    ['Name', ['Alpha', 'Missing', 'Zeta'], ['Zeta', 'Missing', 'Alpha']],
    ['Color', ['Alpha', 'Zeta', 'Missing'], ['Zeta', 'Alpha', 'Missing']],
    ['Weight', ['Alpha', 'Missing', 'Zeta'], ['Zeta', 'Missing', 'Alpha']],
    ['Diameter', ['Alpha', 'Missing', 'Zeta'], ['Zeta', 'Missing', 'Alpha']],
    ['Nozzle', ['Alpha', 'Zeta', 'Missing'], ['Zeta', 'Alpha', 'Missing']],
    ['Bed', ['Alpha', 'Zeta', 'Missing'], ['Zeta', 'Alpha', 'Missing']],
    ['Traits', ['Alpha', 'Zeta', 'Missing'], ['Zeta', 'Alpha', 'Missing']],
  ])('cycles OFD %s using values, leaving absent ranges last', async (label, ascending, descending) => {
    const user = userEvent.setup();
    render(<OpenFilamentDbBrowserModal isOpen onClose={vi.fn()} />);
    await user.click(screen.getByRole('button', { name: /Test brand/ }));
    await user.click(screen.getByRole('button', { name: /PLA/ }));
    await cycle(screen.getByRole('grid'), label, ascending, descending, defaultOrder, 1);
    expect(filaments.map(item => item.entryId)).toEqual(defaultOrder);
  });

  it('preserves OFD selection through sorting/filtering and resets sorting on Back', async () => {
    const user = userEvent.setup();
    render(<OpenFilamentDbBrowserModal isOpen onClose={vi.fn()} />);
    await user.click(screen.getByRole('button', { name: /Test brand/ }));
    await user.click(screen.getByRole('button', { name: /PLA/ }));
    await user.click(screen.getByRole('button', { name: 'Weight' }));
    await user.click(screen.getByRole('checkbox', { name: 'Select Alpha Blue' }));
    await user.selectOptions(screen.getByRole('combobox', { name: 'Filter by color' }), 'Yellow');
    await user.click(screen.getByRole('checkbox', { name: 'Select all filaments' }));
    await user.click(screen.getByRole('button', { name: 'Back' }));
    await user.click(screen.getByRole('button', { name: /PLA/ }));
    expect(screen.getByRole('columnheader', { name: 'Weight' })).toHaveAttribute('aria-sort', 'none');
    expect(order(screen.getByRole('grid'), 1)).toEqual(defaultOrder);
    await user.click(screen.getByRole('button', { name: 'Import 2 Filaments' }));
    expect(mocks.importFilaments).toHaveBeenCalledWith({ entries: [filaments[1], filaments[0]] });
  });

  it.each([
    ['Name', ['Alpha', 'Missing', 'Zeta'], ['Zeta', 'Missing', 'Alpha']],
    ['Type', ['Alpha', 'Missing', 'Zeta'], ['Zeta', 'Missing', 'Alpha']],
    ['Size', ['Alpha', 'Missing', 'Zeta'], ['Zeta', 'Missing', 'Alpha']],
    ['Tags', ['Alpha', 'Zeta', 'Missing'], ['Zeta', 'Alpha', 'Missing']],
    ['Uploaded', ['Alpha', 'Missing', 'Zeta'], ['Zeta', 'Missing', 'Alpha']],
  ])('cycles model %s by raw values without changing its source array', async (label, ascending, descending) => {
    render(<MemoryRouter><ModelListView models={models} isLoading={false} onViewerModel={vi.fn()} onTagModel={vi.fn()} formatFileSize={size => `${size} bytes`} /></MemoryRouter>);
    await cycle(screen.getByRole('table'), label, ascending, descending);
    expect(models.map(model => model.name)).toEqual(defaultOrder);
  });
});
