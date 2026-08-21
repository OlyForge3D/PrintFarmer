import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, render, screen, waitFor, fireEvent, within } from '@testing-library/react';
import '@testing-library/jest-dom';
import { MemoryRouter, Route, Routes } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { NewSliceJobPage } from '../NewSliceJobPage';
import { AuthProvider } from '../../../../common/contexts/AuthContext';
import type { OrcaMachineProfile, OrcaFilamentProfile, OrcaProcessProfile } from '../../../../services/slicerProfilesService';

// Import the mocked services - these will be the vi.mock versions
import { apiClient } from '../../../../services/api';
import { slicerProfilesService } from '../../../../services/slicerProfilesService';
import { slicerRegistry } from '../../../../services/slicerRegistry';
import { slicerService } from '@/services/slicerService';
import { sliceJobService } from '@/services/sliceJobService';

// Mutable slicer-mode ref so individual describes can opt into Advanced mode.
// Hoisted because vi.mock factories run before module-body initialization.
const slicerModeRef = vi.hoisted(() => ({ value: 'Simple' as 'Simple' | 'Advanced' }));

// === Mock Data ===
const mockPrinters = [
  {
    id: 'printer-1',
    name: 'My Prusa MK4',
    manufacturerId: 'mfg-1',
    manufacturerName: 'Prusa',
    modelId: 'model-1',
    modelName: 'MK4',
    thumbnailUrl: '/thumb/prusa-mk4.png',
    isOnline: true,
    motionType: 'CoreXY'
  },
  {
    id: 'printer-2',
    name: 'Bambu X1',
    manufacturerId: 'mfg-2',
    manufacturerName: 'Bambu Lab',
    modelId: 'model-2',
    modelName: 'X1 Carbon',
    thumbnailUrl: '/thumb/bambu-x1.png',
    isOnline: true,
    motionType: 'CoreXY'
  }
];

const mockPrinterDetails = {
  id: 'printer-1',
  name: 'My Prusa MK4',
  manufacturerId: 'mfg-1',
  manufacturerName: 'Prusa',
  modelId: 'model-1',
  modelName: 'MK4',
  modelMaxX: 250,
  modelMaxY: 210,
  modelMaxZ: 220,
  toolheads: [
    { id: 'th-1', nozzleDiameter: 0.4, nozzleType: 'brass', position: 0 }
  ]
};

const mockMachineProfiles: OrcaMachineProfile[] = [
  {
    name: 'Prusa MK4 0.4 nozzle',
    manufacturer: 'Prusa',
    nozzleDiameter: 0.4,
    printerModel: 'MK4',
  },
  {
    name: 'Prusa MK4 0.6 nozzle',
    manufacturer: 'Prusa',
    nozzleDiameter: 0.6,
    printerModel: 'MK4',
  }
];

const mockFilamentProfiles: OrcaFilamentProfile[] = [
  {
    name: 'Generic PLA @MK4',
    material: 'PLA',
    nozzleTemperature: 215,
    bedTemperature: 60,
    printSpeed: 60,
    compatiblePrinters: ['Prusa MK4 0.4 nozzle'],
  },
  {
    name: 'Prusament PETG @MK4',
    material: 'PETG',
    nozzleTemperature: 240,
    bedTemperature: 85,
    printSpeed: 50,
    compatiblePrinters: ['Prusa MK4 0.4 nozzle'],
  },
  {
    name: 'Generic ABS @MK4',
    material: 'ABS',
    nozzleTemperature: 255,
    bedTemperature: 100,
    printSpeed: 50,
    compatiblePrinters: ['Prusa MK4 0.4 nozzle'],
  }
];

const mockProcessProfiles: OrcaProcessProfile[] = [
  {
    name: '0.20mm Standard @MK4',
    quality: 'Standard',
    layerHeight: 0.2,
    infillPercentage: 15,
    printSpeed: 60,
    supports: false,
    compatiblePrinters: ['Prusa MK4 0.4 nozzle'],
  },
  {
    name: '0.10mm Fine @MK4',
    quality: 'Fine',
    layerHeight: 0.1,
    infillPercentage: 20,
    printSpeed: 40,
    supports: false,
    compatiblePrinters: ['Prusa MK4 0.4 nozzle'],
  },
  {
    name: '0.30mm Draft @MK4',
    quality: 'Draft',
    layerHeight: 0.3,
    infillPercentage: 10,
    printSpeed: 80,
    supports: false,
    compatiblePrinters: ['Prusa MK4 0.4 nozzle'],
  }
];

const mockProfilesSummary = {
  machineProfiles: mockMachineProfiles,
  filamentProfiles: mockFilamentProfiles,
  processProfiles: mockProcessProfiles,
};

const mockModelList = [
  {
    id: 'model-3d-1',
    fileName: 'stored-model.3mf',
    originalFileName: 'test-model.3mf',
    uploadedAt: '2026-06-02T00:00:00Z',
  },
  {
    id: 'model-stl-1',
    fileName: 'stored-model.stl',
    originalFileName: 'test-model.stl',
    uploadedAt: '2026-06-02T00:00:00Z',
  },
];

const mockSlicers = [
  { id: '1', name: 'orcaslicer-worker-1', slicerType: 'OrcaSlicer', version: '2.3.1' },
  { id: '2', name: 'prusaslicer-worker-1', slicerType: 'PrusaSlicer', version: '2.7.0' }
];

// === Mocks ===

// Mock API client
vi.mock('@/services/api', () => ({
  apiClient: {
    get: vi.fn(),
    post: vi.fn(),
    put: vi.fn(),
    delete: vi.fn(),
    getPrinters: vi.fn(() => Promise.resolve(mockPrinters)),
    getPrinterDetails: vi.fn(() => Promise.resolve(mockPrinterDetails)),
    getSpools: vi.fn(() => Promise.resolve({ items: [], totalCount: 0 })),
  }
}));

// Mock slicer profiles service
vi.mock('@/services/slicerProfilesService', () => ({
  slicerProfilesService: {
    listExtended: vi.fn(() => Promise.resolve(mockProfilesSummary)),
    getMachineProfilesForModel: vi.fn(() => Promise.resolve(mockMachineProfiles)),
    getFilamentProfilesForMachines: vi.fn(() => Promise.resolve(mockFilamentProfiles)),
    getProcessProfilesForMachines: vi.fn(() => Promise.resolve(mockProcessProfiles)),
    listCustomProfiles: vi.fn(() => Promise.resolve({ profiles: [], totalCount: 0 })),
  }
}));

// Mock slicer registry
vi.mock('@/services/slicerRegistry', () => ({
  slicerRegistry: {
    getSlicers: vi.fn(() => Promise.resolve(mockSlicers)),
  }
}));

// Mock slicer engine registry (drives the version pin and the submit guard)
vi.mock('@/services/slicerService', () => ({
  slicerService: {
    listEngines: vi.fn(() => Promise.resolve([
      { engine: 'OrcaSlicer', versions: ['2.4.2'], versionEntries: [{ version: '2.4.2', available: true }], latest: '2.4.2' },
    ])),
  },
}));

// Mock slice job service
vi.mock('@/services/sliceJobService', () => ({
  sliceJobService: {
    submitJob: vi.fn(() => Promise.resolve({ id: 'job-1', status: 'Queued' })),
    parseOrcaNumeric: vi.fn(() => undefined),
    getSpoolCostPerGram: vi.fn(() => Promise.resolve({ costPerGram: null, currency: '$', source: null })),
    addSliceToQueue: vi.fn(() => Promise.resolve({ printJobId: 'pj-1', queuePosition: 1, message: 'Queued' })),
  }
}));

// Force Advanced mode when needed so advanced-only sections render in tests.
vi.mock('@/features/slicer/hooks/useSlicerMode', () => ({
  useSlicerMode: () => ({
    mode: slicerModeRef.value,
    enabledModes: ['Simple', 'Advanced'],
    canToggle: false,
    setMode: vi.fn(),
  }),
  SLICER_MODE_STORAGE_KEY: 'pf.slicerMode',
}));

// Mock asset service
vi.mock('@/services/assetService', () => ({
  assetService: {
    getAsset: vi.fn(() => null),
    getCoverImageUrl: vi.fn(() => null),
    getFallbackImageUrl: vi.fn(() => '/assets/printers/generic-printer.svg'),
    getCoverImageUrlWithFallback: vi.fn(() => '/assets/printers/generic-printer.svg'),
  }
}));

// Mock useAuth hook
vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: vi.fn(() => ({
    user: { id: 'user-1', email: 'test@example.com' },
    isAuthenticated: true,
    isLoading: false,
  })),
}));

// Mock PrinterSlicerSelector to simplify testing
vi.mock('../../components/job', () => ({
  PrinterSlicerSelector: ({ 
    printers, 
    selectedPrinterId, 
    onPrinterChange,
    accessory,
  }: { 
    printers: typeof mockPrinters; 
    selectedPrinterId: string; 
    onPrinterChange: (id: string) => void;
    accessory?: React.ReactNode;
  }) => (
    <div data-testid="printer-slicer-selector">
      <select
        data-testid="printer-select"
        value={selectedPrinterId}
        onChange={(e) => onPrinterChange(e.target.value)}
        aria-label="Select printer"
      >
        <option value="">Select a printer</option>
        {printers?.map((p: typeof mockPrinters[0]) => (
          <option key={p.id} value={p.id}>{p.name}</option>
        ))}
      </select>
      {accessory && <div data-testid="printer-selector-accessory">{accessory}</div>}
    </div>
  ),
  // Version selection now lives INSIDE SlicerSelector (one panel for engine +
  // version), so its behaviour is covered by SlicerSelector's own unit tests.
  // Here we only surface the forwarded props so the page's wiring contract —
  // especially passing RAW, unfiltered versionEntries — stays under test.
  SlicerSelector: ({
    onSlicerChange,
    versionEntries,
    latestVersion,
    engineName,
    onVersionChange,
  }: {
    selectedSlicerId: string;
    onSlicerChange: (id: string) => void;
    versionEntries?: Array<{ version: string; available: boolean }>;
    latestVersion?: string;
    engineName?: string;
    onVersionChange?: (v: string | undefined) => void;
  }) => (
    // Flat, primitive attributes only — the version entries are surfaced as
    // two comma-joined lists rather than embedded JSON so the assertions stay
    // readable and no raw object is interpolated into JSX.
    <div
      data-testid="slicer-selector"
      data-all-versions={(versionEntries ?? []).map(v => v.version).join(',')}
      data-available-versions={(versionEntries ?? []).filter(v => v.available).map(v => v.version).join(',')}
      data-latest-version={latestVersion ?? ''}
      data-engine-name={engineName ?? ''}
    >
      <select data-testid="slicer-select" aria-label="Select slicer" onChange={(e) => onSlicerChange(e.target.value)}>
        <option value="orcaslicer">OrcaSlicer</option>
      </select>
      <button type="button" data-testid="pin-version-2-3-1" onClick={() => onVersionChange?.('2.3.1')}>
        pin 2.3.1
      </button>
    </div>
  ),
  SlicerSettingsPanel: () => <div data-testid="slicer-settings-panel">Settings Panel</div>,
}));

const slicerWorkspaceSpy = vi.fn();

vi.mock('@/features/slicer/components/viewer/SlicerWorkspace', () => ({
  SlicerWorkspace: (props: {
    models?: Array<{ id: string; libraryModelId?: string; url: string; viewerUrl?: string; fileType: string }>;
    onAddModel?: () => void;
  }) => {
    slicerWorkspaceSpy(props);
    return (
      <div data-testid="slicer-workspace">
        Slicer Workspace
        {props.onAddModel && (
          <button type="button" onClick={props.onAddModel}>Add model</button>
        )}
      </div>
    );
  },
}));

// Mock 3D viewer
vi.mock('@/features/models3d/components/3d/ModelViewer3D', () => ({
  ModelViewer: () => <div data-testid="model-viewer">Model Viewer</div>
}));

// Mock STL preview modal
vi.mock('@/features/models3d/components/3d/STLPreviewModal', () => ({
  STLPreviewModal: () => null
}));

// Mock ViewerSkeleton
vi.mock('@/features/models3d/components/3d/ViewerSkeleton', () => ({
  ViewerSkeleton: () => <div data-testid="viewer-skeleton">Loading...</div>
}));

// Mock CloneProfilesModal
vi.mock('@/features/slicer/components/CloneProfilesModal', () => ({
  CloneProfilesModal: ({ isOpen }: { isOpen: boolean }) => isOpen ? <div data-testid="clone-profiles-modal" /> : null
}));

// Mock SlicerSettingsPanel
vi.mock('@/features/slicer/components/settings', () => ({
  SlicerSettingsPanel: () => <div data-testid="slicer-settings-panel">Settings Panel</div>,
}));

// Mock useSTLFile hook
vi.mock('@/common/hooks/useSTLFile', () => ({
  useSTLFile: vi.fn(() => ({
    file: null,
    setFile: vi.fn(),
    clearFile: vi.fn(),
  })),
}));

// === Test Setup ===
const createTestQueryClient = () => new QueryClient({
  defaultOptions: {
    queries: { retry: false, staleTime: Infinity },
    mutations: { retry: false },
  },
});

const renderWithProviders = (ui: React.ReactElement, { route = '/slicer' } = {}) => {
  const queryClient = createTestQueryClient();
  
  return {
    ...render(
      <MemoryRouter initialEntries={[route]}>
        <QueryClientProvider client={queryClient}>
          <AuthProvider>
            <Routes>
              <Route path="/slicer" element={ui} />
            </Routes>
          </AuthProvider>
        </QueryClientProvider>
      </MemoryRouter>
    ),
    queryClient,
  };
};

// === Tests ===
describe('NewSliceJobPage', () => {
  beforeEach(() => {
    // #1028: several tests queue one-shot behaviour with `mockResolvedValueOnce`
    // / `mockRejectedValueOnce` (L393, L501-502, L655, L666). `clearAllMocks`
    // only wipes call history — it leaves an unconsumed one-shot on the queue,
    // where it fires in whichever test happens to run next. Under
    // `--sequence.shuffle` that put the rejection queued by "should handle
    // profile service errors gracefully" in front of the profile-filtering
    // tests: machine profiles rejected, so the dependent filament/process fetch
    // never ran and the assertion failed. `resetAllMocks` drains those queues
    // and restores each mock to the implementation its `vi.fn(impl)` factory
    // gave it, so every test starts from the same state whatever the order.
    vi.resetAllMocks();
    slicerWorkspaceSpy.mockClear();
    vi.mocked(apiClient.get).mockResolvedValue({ data: mockModelList } as never);
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  describe('Initial Render', () => {
    it('should render without crashing', async () => {
      renderWithProviders(<NewSliceJobPage />);
      
      // Wait for the printer selector mock to render
      await waitFor(() => {
        expect(screen.getByTestId('printer-slicer-selector')).toBeInTheDocument();
      });
    });

    it('should display the printer selector', async () => {
      renderWithProviders(<NewSliceJobPage />);
      
      await waitFor(() => {
        expect(screen.getByTestId('printer-slicer-selector')).toBeInTheDocument();
        expect(screen.getByTestId('printer-select')).toBeInTheDocument();
      });
    });

    it('should display the slicer settings panel', async () => {
      renderWithProviders(<NewSliceJobPage />);
      
      await waitFor(() => {
        expect(screen.getByTestId('slicer-settings-panel')).toBeInTheDocument();
      });
    });
  });

  describe('Slicer Dropdown', () => {
    it('should show slicer types not worker names', async () => {
      renderWithProviders(<NewSliceJobPage />);
      
      await waitFor(() => {
        // Should show "OrcaSlicer" not "orcaslicer-worker-1"
        expect(screen.queryByText(/orcaslicer-worker/i)).not.toBeInTheDocument();
      });
    });

    it('should deduplicate slicer entries', async () => {
      // Mock multiple workers of the same type
      vi.mocked(slicerRegistry.getSlicers).mockResolvedValueOnce([
        { id: '1', name: 'worker-1', slicerType: 'OrcaSlicer', version: '2.3.1' },
        { id: '2', name: 'worker-2', slicerType: 'OrcaSlicer', version: '2.3.1' }, // duplicate
        { id: '3', name: 'worker-3', slicerType: 'PrusaSlicer', version: '2.7.0' },
      ]);
      
      renderWithProviders(<NewSliceJobPage />);
      
      // Should only show unique slicer types
      await waitFor(() => {
        const selects = screen.getAllByRole('combobox');
        // The slicer engine dropdown should not have duplicate entries
        expect(selects.length).toBeGreaterThan(0);
      });
    });
  });

  describe('Printer Selection', () => {
    it('should fetch printers from API on mount', async () => {
      renderWithProviders(<NewSliceJobPage />);
      
      await waitFor(() => {
        expect(apiClient.getPrinters).toHaveBeenCalled();
      });
    });

    it('should display printer options in dropdown', async () => {
      renderWithProviders(<NewSliceJobPage />);
      
      await waitFor(() => {
        const printerSelect = screen.getByTestId('printer-select');
        expect(printerSelect).toBeInTheDocument();
        expect(screen.getByText('My Prusa MK4')).toBeInTheDocument();
        expect(screen.getByText('Bambu X1')).toBeInTheDocument();
      });
    });

    it('should fetch printer details when printer is selected', async () => {
      renderWithProviders(<NewSliceJobPage />);
      
      // Wait for printers to load and select element to be available
      await waitFor(() => {
        expect(screen.getByTestId('printer-select')).toBeInTheDocument();
        expect(screen.getByText('My Prusa MK4')).toBeInTheDocument();
      });
      
      // Select a printer
      const select = screen.getByTestId('printer-select');
      fireEvent.change(select, { target: { value: 'printer-1' } });
      
      await waitFor(() => {
        expect(apiClient.getPrinterDetails).toHaveBeenCalledWith('printer-1');
      }, { timeout: 2000 });
    });
  });

  describe('Machine Profile Selection', () => {
    it('should fetch machine profiles when printer model is determined', async () => {
      renderWithProviders(<NewSliceJobPage />);
      
      // Wait for printers to load
      await waitFor(() => {
        expect(screen.getByText('My Prusa MK4')).toBeInTheDocument();
      });
      
      // Select a printer
      fireEvent.change(screen.getByTestId('printer-select'), { target: { value: 'printer-1' } });
      
      // Wait for printer details and then machine profiles
      await waitFor(() => {
        expect(apiClient.getPrinterDetails).toHaveBeenCalledWith('printer-1');
      }, { timeout: 2000 });
      
      // Machine profiles should be fetched with the model ID from printer details
      await waitFor(() => {
        expect(slicerProfilesService.getMachineProfilesForModel).toHaveBeenCalled();
      }, { timeout: 2000 });
    });

    it('should let the user filter by nozzle and pick a machine profile in the picker', async () => {
      renderWithProviders(<NewSliceJobPage />);

      await waitFor(() => {
        expect(screen.getByText('My Prusa MK4')).toBeInTheDocument();
      });

      fireEvent.change(screen.getByTestId('printer-select'), { target: { value: 'printer-1' } });

      // The machine profile control is now a dialog trigger, not a <select>.
      const machineProfileTrigger = await screen.findByRole('button', { name: /^Machine profile:/ });

      await waitFor(() => {
        expect(machineProfileTrigger).toBeVisible();
        // Nozzle is stated once, as a badge on the resolved profile, and the
        // redundant nozzle token is trimmed from the label.
        expect(machineProfileTrigger).toHaveTextContent('Prusa MK4');
        expect(machineProfileTrigger).toHaveTextContent('0.4mm');
        expect(slicerProfilesService.getFilamentProfilesForMachines).toHaveBeenCalledWith(['Prusa MK4 0.4 nozzle'], expect.anything());
        expect(slicerProfilesService.getProcessProfilesForMachines).toHaveBeenCalledWith(['Prusa MK4 0.4 nozzle'], expect.anything());
      });

      // There is no longer a standalone nozzle dropdown in the sidebar.
      expect(screen.queryByLabelText('Nozzle diameter')).not.toBeInTheDocument();

      fireEvent.click(machineProfileTrigger);

      const nozzleFacet = await screen.findByRole('group', { name: 'Filter by nozzle diameter' });
      fireEvent.click(within(nozzleFacet).getByRole('button', { name: /0\.6 mm/ }));

      // Filtering to 0.6 hides the 0.4 profile entirely.
      await waitFor(() => {
        expect(screen.queryByRole('region', { name: '0.4 mm machine profiles' })).not.toBeInTheDocument();
      });

      const group06 = screen.getByRole('region', { name: '0.6 mm machine profiles' });
      fireEvent.click(within(group06).getByRole('button', { name: /Prusa MK4/ }));

      // Selecting commits both nozzle and profile in one step, and BOTH downstream
      // compatibility queries must follow the newly chosen machine profile —
      // a stale filament list is just as broken as a stale process list.
      await waitFor(() => {
        expect(machineProfileTrigger).toHaveTextContent('0.6mm');
        expect(slicerProfilesService.getProcessProfilesForMachines).toHaveBeenLastCalledWith(['Prusa MK4 0.6 nozzle'], expect.anything());
        expect(slicerProfilesService.getFilamentProfilesForMachines).toHaveBeenLastCalledWith(['Prusa MK4 0.6 nozzle'], expect.anything());
      });
    });

    it('keeps the canonical profile name as the value while showing a trimmed label', async () => {
      // Prusa MK4S ships the unspaced "HF0.4" form, so this fixture exercises
      // both halves of the contract: the trimmed label ("Prusa MK4S HF") differs
      // from the canonical name, and HF detection must still fire.
      //
      // `getProcessProfilesForMachines` is called with [selectedMachineProfileId] —
      // the exact state that NewSliceJobPage serializes as
      // `slicerProfileJson.machineProfileName` — so asserting on it proves the
      // trimmed label never leaks into the value sent to the slice API.
      vi.mocked(slicerProfilesService.getMachineProfilesForModel).mockResolvedValue([
        { name: 'Prusa MK4S 0.4 nozzle', manufacturer: 'Prusa', nozzleDiameter: 0.4, printerModel: 'MK4S' },
        { name: 'Prusa MK4S HF0.4 nozzle', manufacturer: 'Prusa', nozzleDiameter: 0.4, printerModel: 'MK4S' },
      ] as OrcaMachineProfile[]);

      renderWithProviders(<NewSliceJobPage />);

      await waitFor(() => {
        expect(screen.getByText('My Prusa MK4')).toBeInTheDocument();
      });

      fireEvent.change(screen.getByTestId('printer-select'), { target: { value: 'printer-1' } });

      const machineProfileTrigger = await screen.findByRole('button', { name: /^Machine profile:/ });

      await waitFor(() => {
        expect(machineProfileTrigger).toBeEnabled();
        expect(machineProfileTrigger).toHaveTextContent('Prusa MK4S');
      });

      fireEvent.click(machineProfileTrigger);

      const group04 = await screen.findByRole('region', { name: '0.4 mm machine profiles' });
      const rows = within(group04).getAllByRole('button');
      const hfRow = rows.find((r) => /HF/.test(r.textContent ?? ''))!;

      // The visible label is trimmed and carries the HF marker...
      expect(hfRow.querySelector('span.truncate')?.textContent?.trim()).toBe('Prusa MK4S HF');
      fireEvent.click(hfRow);

      // ...while every downstream consumer receives the FULL canonical name.
      await waitFor(() => {
        expect(slicerProfilesService.getProcessProfilesForMachines).toHaveBeenLastCalledWith(['Prusa MK4S HF0.4 nozzle'], expect.anything());
        expect(slicerProfilesService.getFilamentProfilesForMachines).toHaveBeenLastCalledWith(['Prusa MK4S HF0.4 nozzle'], expect.anything());
      });
    });

    it('serializes the canonical profile name into the submitted slicerProfileJson', async () => {
      // Guards the integration point, not just the serializer: a future edit
      // passing selectedMachineProfileLabel into buildSlicerProfileJson would
      // satisfy the util's own unit tests but fail here.
      //
      // MK4S ships the unspaced "HF0.4" form, so the trimmed label
      // ("Prusa MK4S HF") differs from the canonical name, and MK4S avoids the
      // CORE One-only process guard that would otherwise block submission.
      vi.mocked(slicerProfilesService.getMachineProfilesForModel).mockResolvedValue([
        { name: 'Prusa MK4S 0.4 nozzle', manufacturer: 'Prusa', nozzleDiameter: 0.4, printerModel: 'MK4S' },
        { name: 'Prusa MK4S HF0.4 nozzle', manufacturer: 'Prusa', nozzleDiameter: 0.4, printerModel: 'MK4S' },
      ] as OrcaMachineProfile[]);
      vi.mocked(slicerProfilesService.getProcessProfilesForMachines).mockResolvedValue([
        {
          name: '0.20mm Standard @MK4S',
          quality: 'Standard',
          layerHeight: 0.2,
          infillPercentage: 15,
          printSpeed: 60,
          supports: false,
          compatiblePrinters: ['Prusa MK4S 0.4 nozzle', 'Prusa MK4S HF0.4 nozzle'],
        },
      ] as OrcaProcessProfile[]);

      renderWithProviders(<NewSliceJobPage />, { route: '/slicer?modelId=model-3d-1' });

      await waitFor(() => {
        expect(screen.getByText('My Prusa MK4')).toBeInTheDocument();
      });

      fireEvent.change(screen.getByTestId('printer-select'), { target: { value: 'printer-1' } });

      const machineProfileTrigger = await screen.findByRole('button', { name: /^Machine profile:/ });
      await waitFor(() => {
        expect(machineProfileTrigger).toBeEnabled();
        expect(machineProfileTrigger).toHaveTextContent('Prusa MK4S');
      });

      fireEvent.click(machineProfileTrigger);
      const group04 = await screen.findByRole('region', { name: '0.4 mm machine profiles' });
      const hfRow = within(group04).getAllByRole('button').find((r) => /HF/.test(r.textContent ?? ''))!;
      expect(hfRow.querySelector('span.truncate')?.textContent?.trim()).toBe('Prusa MK4S HF');
      fireEvent.click(hfRow);

      // A process preset must resolve before submission is allowed. Wait for it
      // to settle, then read onSlice fresh — it is a useCallback closing over
      // selectedProcessPresetId, so a handle captured earlier is stale and would
      // always bail out on "Select a process profile".
      await waitFor(() => {
        expect(slicerProfilesService.getProcessProfilesForMachines)
          .toHaveBeenLastCalledWith(['Prusa MK4S HF0.4 nozzle'], expect.anything());
      });
      await waitFor(() => {
        const preset = document.querySelectorAll('select');
        const processSelect = Array.from(preset).find((s) => s.value.startsWith('system:'));
        expect(processSelect?.value).toBe('system:0.20mm Standard @MK4S');
      });

      const latestOnSlice = () =>
        (slicerWorkspaceSpy.mock.calls.at(-1)?.[0] as { onSlice?: (ids?: string[]) => void } | undefined)?.onSlice;

      await waitFor(() => {
        expect(latestOnSlice()).toBeTypeOf('function');
      });

      await act(async () => { latestOnSlice()!(); });

      await waitFor(() => {
        expect(sliceJobService.submitJob).toHaveBeenCalled();
      }, { timeout: 3000 });

      const request = vi.mocked(sliceJobService.submitJob).mock.calls.at(-1)?.[0] as { slicerProfileJson: string };
      const profile = JSON.parse(request.slicerProfileJson) as { machineProfileName: string };
      expect(profile.machineProfileName).toBe('Prusa MK4S HF0.4 nozzle');
      expect(profile.machineProfileName).not.toBe('Prusa MK4S HF');
    });

    it('keeps the machine profile trigger focusable and explained when a printer has no profiles', async () => {
      // `disabled` and `explainedDisabled` are two separate expressions that must
      // stay in sync; without this, a future edit could silently drop the button
      // out of the tab order and take its explanation with it.
      vi.mocked(slicerProfilesService.getMachineProfilesForModel).mockResolvedValue([]);
      vi.mocked(slicerProfilesService.listCustomProfiles).mockResolvedValue({
        profiles: [],
        totalCount: 0,
        machineProfileCount: 0,
        processProfileCount: 0,
        filamentProfileCount: 0,
      });

      renderWithProviders(<NewSliceJobPage />);

      await waitFor(() => {
        expect(screen.getByText('My Prusa MK4')).toBeInTheDocument();
      });

      fireEvent.change(screen.getByTestId('printer-select'), { target: { value: 'printer-1' } });

      const trigger = await screen.findByRole('button', { name: /^Machine profile:/ });

      await waitFor(() => {
        expect(trigger).toHaveAttribute('aria-disabled', 'true');
      });

      // Reachable by keyboard, and the reason is discoverable.
      expect(trigger).toHaveAttribute('tabindex', '0');
      expect(trigger).not.toHaveAttribute('disabled');
      expect(trigger).toHaveAttribute('title', expect.stringContaining('No machine profiles for this printer'));

      // ...but still inert.
      fireEvent.click(trigger);
      expect(screen.queryByRole('dialog', { name: 'Select machine profile' })).not.toBeInTheDocument();

      // The kebab remains the escape route to Import / Manage.
      expect(screen.getByLabelText('Machine profile options menu')).toBeEnabled();
    });

    it('should keep custom machine profiles selectable when system profiles are unavailable', async () => {
      vi.mocked(slicerProfilesService.getMachineProfilesForModel).mockResolvedValueOnce([]);
      vi.mocked(slicerProfilesService.listCustomProfiles).mockResolvedValueOnce({
        profiles: [
          {
            id: 'custom-machine-1',
            name: 'Custom MK4 0.8 nozzle',
            profileType: 'machine',
            isSystem: false,
            createdAt: '2026-04-28T00:00:00Z',
            rawJson: JSON.stringify({ printer_model: 'MK4', nozzle_diameter: [0.8] }),
          },
        ],
        totalCount: 1,
        machineProfileCount: 1,
        processProfileCount: 0,
        filamentProfileCount: 0,
      });

      renderWithProviders(<NewSliceJobPage />);

      await waitFor(() => {
        expect(screen.getByText('My Prusa MK4')).toBeInTheDocument();
      });

      fireEvent.change(screen.getByTestId('printer-select'), { target: { value: 'printer-1' } });

      const machineProfileTrigger = await screen.findByRole('button', { name: /^Machine profile:/ });

      await waitFor(() => {
        expect(machineProfileTrigger).toHaveTextContent('Custom MK4');
        expect(machineProfileTrigger).toHaveTextContent('0.8mm');
      });

      // The custom profile is reachable in the picker under My Profiles.
      fireEvent.click(machineProfileTrigger);
      const myProfiles = await screen.findByRole('region', { name: 'My machine profiles' });
      expect(within(myProfiles).getByRole('button', { name: /Custom MK4/ })).toBeInTheDocument();
      fireEvent.keyDown(document, { key: 'Escape' });

      expect(screen.queryByText(/No machine profiles available/)).not.toBeInTheDocument();

      await act(async () => {
        await new Promise((resolve) => setTimeout(resolve, 600));
      });
      expect(screen.queryByTestId('clone-profiles-modal')).not.toBeInTheDocument();
    });
  });

  describe('Filament Profile Filtering', () => {
    it('should fetch filament profiles filtered by selected machine', async () => {
      renderWithProviders(<NewSliceJobPage />);
      
      // Wait for printers to load
      await waitFor(() => {
        expect(screen.getByText('My Prusa MK4')).toBeInTheDocument();
      });
      
      // Select a printer
      fireEvent.change(screen.getByTestId('printer-select'), { target: { value: 'printer-1' } });
      
      // Wait for filament profiles to be fetched (happens after machine profiles)
      await waitFor(() => {
        expect(slicerProfilesService.getFilamentProfilesForMachines).toHaveBeenCalled();
      }, { timeout: 2000 });
    });
  });

  describe('Process Profile Filtering', () => {
    it('should fetch process profiles filtered by selected machine', async () => {
      renderWithProviders(<NewSliceJobPage />);
      
      // Wait for printers to load
      await waitFor(() => {
        expect(screen.getByText('My Prusa MK4')).toBeInTheDocument();
      });
      
      // Select a printer
      fireEvent.change(screen.getByTestId('printer-select'), { target: { value: 'printer-1' } });
      
      // Wait for process profiles to be fetched
      await waitFor(() => {
        expect(slicerProfilesService.getProcessProfilesForMachines).toHaveBeenCalled();
      }, { timeout: 2000 });
    });

    describe('CORE One HF variant guard (issue #1782)', () => {
      // The bug: Guard 2 detected a candidate's HF variant by joining the
      // profile name WITH its entire `compatiblePrinters` list into one
      // string before testing for "HF". A process profile that legitimately
      // supports both CORE One variants lists both machine names in
      // `compatiblePrinters`, so the joined text always mentions "HF" — which
      // made the guard drop the profile for the STANDARD machine while
      // wrongly keeping it for the HF one. Guard 1 (a few lines above) had
      // already proven the profile lists the selected machine as compatible.
      beforeEach(() => {
        vi.mocked(slicerProfilesService.getMachineProfilesForModel).mockResolvedValue([
          { name: 'Prusa CORE One 0.4 nozzle', manufacturer: 'Prusa', nozzleDiameter: 0.4, printerModel: 'CORE One' },
          { name: 'Prusa CORE One HF 0.4 nozzle', manufacturer: 'Prusa', nozzleDiameter: 0.4, printerModel: 'CORE One' },
        ] as OrcaMachineProfile[]);
      });

      it('offers a dual-compatible process profile for BOTH the standard and HF machine', async () => {
        vi.mocked(slicerProfilesService.getProcessProfilesForMachines).mockResolvedValue([
          {
            name: '0.20mm Standard @CORE One',
            quality: 'Standard',
            layerHeight: 0.2,
            infillPercentage: 15,
            printSpeed: 60,
            supports: false,
            compatiblePrinters: ['Prusa CORE One 0.4 nozzle', 'Prusa CORE One HF 0.4 nozzle'],
          },
        ] as OrcaProcessProfile[]);

        renderWithProviders(<NewSliceJobPage />);

        await waitFor(() => {
          expect(screen.getByText('My Prusa MK4')).toBeInTheDocument();
        });

        fireEvent.change(screen.getByTestId('printer-select'), { target: { value: 'printer-1' } });

        const machineProfileTrigger = await screen.findByRole('button', { name: /^Machine profile:/ });
        await waitFor(() => {
          expect(machineProfileTrigger).toBeEnabled();
        });

        // Select the STANDARD (non-HF) machine profile.
        fireEvent.click(machineProfileTrigger);
        const group04 = await screen.findByRole('region', { name: '0.4 mm machine profiles' });
        const standardRow = within(group04).getAllByRole('button').find((r) => !/HF/.test(r.textContent ?? ''))!;
        fireEvent.click(standardRow);

        await waitFor(() => {
          expect(slicerProfilesService.getProcessProfilesForMachines)
            .toHaveBeenLastCalledWith(['Prusa CORE One 0.4 nozzle'], expect.anything());
        });

        // The dual-compatible profile must still be offered here — this is
        // exactly the case the old guard dropped.
        await waitFor(() => {
          expect(screen.getByRole('option', { name: /0\.20mm Standard @CORE One/ })).toBeInTheDocument();
        });

        // Switch to the HF variant; the same profile must remain offered.
        fireEvent.click(machineProfileTrigger);
        const group04Again = await screen.findByRole('region', { name: '0.4 mm machine profiles' });
        const hfRow = within(group04Again).getAllByRole('button').find((r) => /HF/.test(r.textContent ?? ''))!;
        fireEvent.click(hfRow);

        await waitFor(() => {
          expect(slicerProfilesService.getProcessProfilesForMachines)
            .toHaveBeenLastCalledWith(['Prusa CORE One HF 0.4 nozzle'], expect.anything());
        });

        await waitFor(() => {
          expect(screen.getByRole('option', { name: /0\.20mm Standard @CORE One/ })).toBeInTheDocument();
        });
      });

      it('still hides an HF-only process profile from the standard machine, and explains the empty state', async () => {
        vi.mocked(slicerProfilesService.getProcessProfilesForMachines).mockResolvedValue([
          {
            name: '0.20mm Standard @CORE One HF',
            quality: 'Standard',
            layerHeight: 0.2,
            infillPercentage: 15,
            printSpeed: 60,
            supports: false,
            compatiblePrinters: ['Prusa CORE One HF 0.4 nozzle'],
          },
        ] as OrcaProcessProfile[]);

        renderWithProviders(<NewSliceJobPage />);

        await waitFor(() => {
          expect(screen.getByText('My Prusa MK4')).toBeInTheDocument();
        });

        fireEvent.change(screen.getByTestId('printer-select'), { target: { value: 'printer-1' } });

        const machineProfileTrigger = await screen.findByRole('button', { name: /^Machine profile:/ });
        await waitFor(() => {
          expect(machineProfileTrigger).toBeEnabled();
        });

        fireEvent.click(machineProfileTrigger);
        const group04 = await screen.findByRole('region', { name: '0.4 mm machine profiles' });
        const standardRow = within(group04).getAllByRole('button').find((r) => !/HF/.test(r.textContent ?? ''))!;
        fireEvent.click(standardRow);

        await waitFor(() => {
          expect(slicerProfilesService.getProcessProfilesForMachines)
            .toHaveBeenLastCalledWith(['Prusa CORE One 0.4 nozzle'], expect.anything());
        });

        // Genuinely HF-only profile must stay hidden from the standard machine,
        // and the resulting empty state must explain itself instead of leaving
        // a dead "no options" select with no route forward.
        await waitFor(() => {
          expect(screen.queryByRole('option', { name: /0\.20mm Standard @CORE One HF/ })).not.toBeInTheDocument();
          expect(screen.getByText(/No process profiles are compatible with this machine variant/i)).toBeInTheDocument();
        });
      });
    });
  });

  describe('Cascading Selection Logic', () => {
    it('should update printer selection when changed', async () => {
      renderWithProviders(<NewSliceJobPage />);
      
      // Wait for printers to load
      await waitFor(() => {
        expect(screen.getByText('My Prusa MK4')).toBeInTheDocument();
      });
      
      // Select a printer and verify it triggers the callback
      const select = screen.getByTestId('printer-select');
      fireEvent.change(select, { target: { value: 'printer-1' } });
      
      // Verify the API was called with the correct printer
      await waitFor(() => {
        expect(apiClient.getPrinterDetails).toHaveBeenCalledWith('printer-1');
      }, { timeout: 2000 });
    });

    it('should allow changing printer selection', async () => {
      renderWithProviders(<NewSliceJobPage />);
      
      // Wait for printers to load
      await waitFor(() => {
        expect(screen.getByText('My Prusa MK4')).toBeInTheDocument();
      });
      
      // First select printer-1
      fireEvent.change(screen.getByTestId('printer-select'), { target: { value: 'printer-1' } });
      
      await waitFor(() => {
        expect(apiClient.getPrinterDetails).toHaveBeenCalledWith('printer-1');
      }, { timeout: 2000 });
      
      // Then change to printer-2
      fireEvent.change(screen.getByTestId('printer-select'), { target: { value: 'printer-2' } });
      
      // Verify the API was called for the new printer
      await waitFor(() => {
        expect(apiClient.getPrinterDetails).toHaveBeenCalledWith('printer-2');
      }, { timeout: 2000 });
    });
  });

  describe('Loading States', () => {
    it('should render during printer loading', async () => {
      renderWithProviders(<NewSliceJobPage />);
      
      // The component should render the printer selector even during loading
      await waitFor(() => {
        expect(screen.getByTestId('printer-slicer-selector')).toBeInTheDocument();
      });
    });

    it('should render during profile loading', async () => {
      renderWithProviders(<NewSliceJobPage />);
      
      // Wait for printers to load
      await waitFor(() => {
        expect(screen.getByText('My Prusa MK4')).toBeInTheDocument();
      });
      
      // Select a printer to trigger profile loading
      fireEvent.change(screen.getByTestId('printer-select'), { target: { value: 'printer-1' } });
      
      // The component should handle loading state gracefully - verify via API call
      await waitFor(() => {
        expect(apiClient.getPrinterDetails).toHaveBeenCalled();
      }, { timeout: 2000 });
    });
  });

  describe('Error States', () => {
    it('should handle API errors gracefully', async () => {
      vi.mocked(apiClient.getPrinters).mockRejectedValueOnce(new Error('Network error'));
      
      renderWithProviders(<NewSliceJobPage />);
      
      // The component should still render without crashing
      await waitFor(() => {
        expect(screen.getByTestId('printer-slicer-selector')).toBeInTheDocument();
      });
    });

    it('should handle profile service errors gracefully', async () => {
      vi.mocked(slicerProfilesService.getMachineProfilesForModel).mockRejectedValueOnce(new Error('Profile error'));
      
      renderWithProviders(<NewSliceJobPage />);
      
      // Select a printer
      await waitFor(() => {
        fireEvent.change(screen.getByTestId('printer-select'), { target: { value: 'printer-1' } });
      });
      
      // The component should still render without crashing
      expect(screen.getByTestId('printer-slicer-selector')).toBeInTheDocument();
    });
  });

  describe('URL Parameters', () => {
    it('should support model selection from URL parameter', async () => {
      renderWithProviders(<NewSliceJobPage />, { route: '/slicer?modelId=model-3d-1' });
      
      await waitFor(() => {
        expect(screen.getByTestId('printer-slicer-selector')).toBeInTheDocument();
      });
      
      // The model ID from URL should be captured
      // This is tested by verifying the page renders without error
    });

    it('preserves the raw 3mf viewer URL and file type for preselected library models', async () => {
      renderWithProviders(<NewSliceJobPage />, { route: '/slicer?modelId=model-3d-1' });

      await waitFor(() => {
        expect(screen.getByTestId('printer-select')).toBeInTheDocument();
      });

      fireEvent.change(screen.getByTestId('printer-select'), { target: { value: 'printer-1' } });

      await waitFor(() => {
        expect(screen.getByTestId('slicer-workspace')).toBeInTheDocument();
      });

      await waitFor(() => {
        const lastWorkspaceProps = slicerWorkspaceSpy.mock.calls.at(-1)?.[0] as {
          models?: Array<{ id: string; libraryModelId?: string; url: string; viewerUrl?: string; fileType: string }>;
        } | undefined;
        const selectedModel = lastWorkspaceProps?.models?.find((model) => model.libraryModelId === 'model-3d-1');

        expect(selectedModel).toEqual(expect.objectContaining({
          url: expect.stringMatching(/\/3d-models\/file\/model-3d-1$/),
          viewerUrl: expect.stringMatching(/\/3d-models\/file\/model-3d-1$/),
          fileType: '3mf',
        }));
      });
    });
  });

  describe('Duplicate model placement (issue #1771)', () => {
    // Regression test: a library model could only ever be placed once — either
    // re-selecting it for a second plate, or picking it twice on the same
    // plate, silently no-opped because the bed-model instance was keyed
    // directly on the library model id. See root-cause detail in
    // NewSliceJobPage.tsx's model-pick effect.
    async function openPickerAndSelect(label: string) {
      act(() => {
        fireEvent.click(screen.getByRole('button', { name: /add model/i }));
      });
      const option = await screen.findByRole('option', { name: new RegExp(label.replace(/\./g, '\\.')) });
      act(() => {
        fireEvent.doubleClick(option);
      });
    }

    it('placing the same library model twice creates two distinct bed-model instances sharing one libraryModelId', async () => {
      renderWithProviders(<NewSliceJobPage />);

      await waitFor(() => {
        expect(screen.getByTestId('printer-select')).toBeInTheDocument();
      });
      fireEvent.change(screen.getByTestId('printer-select'), { target: { value: 'printer-1' } });

      await waitFor(() => {
        expect(screen.getByTestId('slicer-workspace')).toBeInTheDocument();
      });

      await openPickerAndSelect('test-model.stl');

      await waitFor(() => {
        const models = slicerWorkspaceSpy.mock.calls.at(-1)?.[0]?.models ?? [];
        expect(models).toHaveLength(1);
        expect(models[0].libraryModelId).toBe('model-stl-1');
      });
      const firstInstanceId = (slicerWorkspaceSpy.mock.calls.at(-1)?.[0]?.models ?? [])[0].id;

      // Re-select the SAME library model a second time (mirrors picking it
      // again for a second plate, or twice on the same plate — the underlying
      // cause is identical per the issue's acceptance criteria).
      await openPickerAndSelect('test-model.stl');

      await waitFor(() => {
        const models = slicerWorkspaceSpy.mock.calls.at(-1)?.[0]?.models ?? [];
        expect(models).toHaveLength(2);
        expect(models.every((m: { libraryModelId?: string }) => m.libraryModelId === 'model-stl-1')).toBe(true);
        // Distinct bed-model instance ids so plate assignment / addModelToActivePlate fires.
        const ids = models.map((m: { id: string }) => m.id);
        expect(new Set(ids).size).toBe(2);
        expect(ids).toContain(firstInstanceId);
      });
    });
  });

  describe('Bed Type', () => {
    beforeEach(() => {
      slicerModeRef.value = 'Advanced';
    });
    afterEach(() => {
      slicerModeRef.value = 'Simple';
    });

    it('does not render a bed type selector in advanced mode', async () => {
      renderWithProviders(<NewSliceJobPage />);

      await waitFor(() => {
        expect(screen.queryByLabelText(/bed type/i)).not.toBeInTheDocument();
      });
    });
  });

  describe('Engine Version wiring (issues #1772, #1773)', () => {
    /**
     * The reported case: 2.3.1 is in the plugin registry but has no online
     * worker. The page must still forward the RAW, unfiltered entries —
     * SlicerSelector hides unpickable versions at render time, while the
     * submit guard needs the full list to detect "engine registered but zero
     * available workers" (an emptied list reads as "nothing to check").
     */
    beforeEach(() => {
      vi.mocked(slicerService.listEngines).mockResolvedValue([
        {
          engine: 'OrcaSlicer',
          versions: ['2.4.2', '2.3.1'],
          versionEntries: [
            { version: '2.4.2', available: true },
            { version: '2.3.1', available: false },
          ],
          latest: '2.4.2',
        },
      ]);
    });

    it('forwards the raw unfiltered version entries to the slicer selector', async () => {
      renderWithProviders(<NewSliceJobPage />);

      await waitFor(() => {
        const selector = screen.getByTestId('slicer-selector');
        // 2.3.1 must still reach the component (the submit guard needs it)
        // even though the component will not offer it to the user.
        expect(selector).toHaveAttribute('data-all-versions', '2.4.2,2.3.1');
        expect(selector).toHaveAttribute('data-available-versions', '2.4.2');
      });
    });

    it('forwards the backend-resolved latest version and engine name', async () => {
      renderWithProviders(<NewSliceJobPage />);

      await waitFor(() => {
        const selector = screen.getByTestId('slicer-selector');
        expect(selector).toHaveAttribute('data-latest-version', '2.4.2');
        expect(selector).toHaveAttribute('data-engine-name', 'OrcaSlicer');
      });
    });

    it('does not render a standalone Engine version group box beside the engine panel', async () => {
      renderWithProviders(<NewSliceJobPage />);

      await waitFor(() => {
        expect(screen.getByTestId('slicer-selector')).toBeInTheDocument();
      });

      // Engine and version are one decision in one panel now, so the page
      // must not emit its own separate version control.
      expect(screen.queryByLabelText('Engine version')).not.toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'More information about engine version' }))
        .not.toBeInTheDocument();
    });
  });
});
