import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CatalogPage } from '@/features/catalog/pages/CatalogPage';

const { useFilamentTypesPagedMock } = vi.hoisted(() => ({
  useFilamentTypesPagedMock: vi.fn(),
}));

vi.mock('@/common/hooks/useApi', () => {
  const query = () => ({ data: [], isLoading: false, isError: false });
  const mutation = () => ({
    mutate: vi.fn(),
    mutateAsync: vi.fn().mockResolvedValue(undefined),
    isPending: false,
  });

  return {
    useManufacturers: query,
    useModels: query,
    useToolheadModels: query,
    useExtruderModels: query,
    useHotendModels: query,
    useNozzleModels: query,
    useNozzleMaterials: query,
    useCreateToolheadModel: mutation,
    useUpdateToolheadModel: mutation,
    useDeleteToolheadModel: mutation,
    useCreateExtruderModel: mutation,
    useUpdateExtruderModel: mutation,
    useDeleteExtruderModel: mutation,
    useCreateHotendModel: mutation,
    useUpdateHotendModel: mutation,
    useDeleteHotendModel: mutation,
    useCreateNozzleModel: mutation,
    useUpdateNozzleModel: mutation,
    useDeleteNozzleModel: mutation,
    useCreateNozzleMaterial: mutation,
    useUpdateNozzleMaterial: mutation,
    useDeleteNozzleMaterial: mutation,
    useFilamentTypesPaged: useFilamentTypesPagedMock,
    useCreateFilamentType: mutation,
    useUpdateFilamentType: mutation,
    useDeleteFilamentType: mutation,
    useImportFilamentTypesFromSpoolman: mutation,
  };
});

vi.mock('@/common/hooks/useCatalogViewMode', () => ({
  useCatalogViewMode: () => ['grid', vi.fn()],
}));

vi.mock('@/features/admin/utils/adminHubParentState', () => ({
  useAdminHubParent: () => undefined,
}));

vi.mock('@/features/models3d/components/EditModelModal', () => ({
  EditModelModal: () => null,
}));

vi.mock('@/common/components/ManufacturerSelector', () => ({
  ManufacturerSelector: ({
    value,
    onChange,
    ariaLabel,
  }: {
    value?: string;
    onChange: (value: string | undefined) => void;
    ariaLabel: string;
  }) => (
    <select
      aria-label={ariaLabel}
      value={value ?? ''}
      onChange={(event) => onChange(event.target.value || undefined)}
    >
      <option value="">All Manufacturers</option>
    </select>
  ),
}));

function renderCatalogPage() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <CatalogPage />
    </QueryClientProvider>,
  );
}

describe('CatalogPage', () => {
  beforeEach(() => {
    useFilamentTypesPagedMock.mockReturnValue({
      data: { items: [], totalCount: 120, totalPages: 3 },
      isLoading: false,
      isError: false,
    });
  });

  it('uses the shared header, toolbar, manufacturer disclosure, and results summary across model catalogs', () => {
    renderCatalogPage();

    const catalogs = [
      { tab: /Printers$/, title: 'Printer Models', label: 'printer models', total: 0, hasManufacturerFilter: true },
      { tab: /Filaments$/, title: 'Filament Types', label: 'filament types', controlsLabel: 'Filament', total: 120, hasManufacturerFilter: false },
      { tab: /Toolheads$/, title: 'Toolhead Models', label: 'toolhead models', total: 0, hasManufacturerFilter: true },
      { tab: /Extruders$/, title: 'Extruder Models', label: 'extruder models', total: 0, hasManufacturerFilter: true },
      { tab: /Hotends$/, title: 'Hotend Models', label: 'hotend models', total: 0, hasManufacturerFilter: true },
      { tab: /Nozzles$/, title: 'Nozzle Models', label: 'nozzle models', total: 0, hasManufacturerFilter: true },
    ];

    for (const catalog of catalogs) {
      fireEvent.click(screen.getByRole('tab', { name: catalog.tab }));

      expect(screen.getByRole('heading', { name: catalog.title, level: 2 })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Add' })).toBeInTheDocument();
      expect(
        screen.getByRole('group', { name: `${catalog.controlsLabel ?? catalog.label} catalog controls` }),
      ).toBeInTheDocument();
      expect(screen.getByRole('status')).toHaveTextContent(`Showing 0 of ${catalog.total} ${catalog.label}`);

      if (catalog.hasManufacturerFilter) {
        fireEvent.click(screen.getByRole('button', { name: 'Filters' }));
        expect(
          screen.getByRole('combobox', { name: `Filter ${catalog.label} by manufacturer` }),
        ).toBeInTheDocument();
      } else {
        expect(screen.queryByRole('button', { name: 'Filters' })).not.toBeInTheDocument();
      }
    }
  });

  it('keeps Filaments on paged, submit-driven server search without model filters', async () => {
    renderCatalogPage();
    fireEvent.click(screen.getByRole('tab', { name: /Filaments$/ }));

    expect(screen.getByRole('heading', { name: 'Filament Types', level: 2 })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Add' })).toBeInTheDocument();
    expect(screen.getByRole('group', { name: 'Filament catalog controls' })).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Showing 0 of 120 filament types');
    expect(screen.queryByRole('button', { name: 'Filters' })).not.toBeInTheDocument();
    expect(useFilamentTypesPagedMock).toHaveBeenLastCalledWith(1, 50, undefined);

    const search = screen.getByRole('searchbox', { name: 'Search filament types' });
    fireEvent.change(search, { target: { value: ' PETG ' } });
    expect(useFilamentTypesPagedMock).toHaveBeenLastCalledWith(1, 50, undefined);

    const form = search.closest('form');
    expect(form).not.toBeNull();
    fireEvent.submit(form!);

    await waitFor(() => {
      expect(useFilamentTypesPagedMock).toHaveBeenLastCalledWith(1, 50, 'PETG');
    });
  });
});
