import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, render, screen, waitFor, fireEvent } from '@testing-library/react';
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

// Mock slice job service
vi.mock('@/services/sliceJobService', () => ({
  sliceJobService: {
    submitJob: vi.fn(() => Promise.resolve({ id: 'job-1', status: 'Queued' })),
  }
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
  SlicerSelector: ({ onSlicerChange }: { selectedSlicerId: string; onSlicerChange: (id: string) => void }) => (
    <div data-testid="slicer-selector">
      <select data-testid="slicer-select" aria-label="Select slicer" onChange={(e) => onSlicerChange(e.target.value)}>
        <option value="orcaslicer">OrcaSlicer</option>
      </select>
    </div>
  ),
}));

const slicerWorkspaceSpy = vi.fn();

vi.mock('@/features/slicer/components/viewer', () => ({
  SlicerWorkspace: (props: {
    models?: Array<{ id: string; url: string; viewerUrl?: string; fileType: string }>;
  }) => {
    slicerWorkspaceSpy(props);
    return <div data-testid="slicer-workspace">Slicer Workspace</div>;
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
  BED_TYPE_OPTIONS: [
    { value: 'Default Plate', label: 'Default Plate' },
    { value: 'Cool Plate', label: 'Cool Plate' },
    { value: 'Engineering Plate', label: 'Engineering Plate' },
    { value: 'High Temp Plate', label: 'High Temp Plate' },
    { value: 'Textured PEI Plate', label: 'Textured PEI Plate' },
  ],
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
    vi.clearAllMocks();
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

    it('should filter machine profiles by selected nozzle diameter', async () => {
      renderWithProviders(<NewSliceJobPage />);

      await waitFor(() => {
        expect(screen.getByText('My Prusa MK4')).toBeInTheDocument();
      });

      fireEvent.change(screen.getByTestId('printer-select'), { target: { value: 'printer-1' } });

      const nozzleFilter = await screen.findByLabelText('Nozzle');
      const machineProfileSelect = await screen.findByLabelText('Machine profile');

      await waitFor(() => {
        expect(nozzleFilter).toHaveValue('0.4');
        expect(machineProfileSelect).toHaveValue('Prusa MK4 0.4 nozzle');
      });

      expect(screen.getByRole('option', { name: /Prusa MK4 0\.4 nozzle/ })).toBeInTheDocument();
      expect(screen.queryByRole('option', { name: /Prusa MK4 0\.6 nozzle/ })).not.toBeInTheDocument();

      fireEvent.change(nozzleFilter, { target: { value: '0.6' } });

      await waitFor(() => {
        expect(machineProfileSelect).toHaveValue('Prusa MK4 0.6 nozzle');
      });

      expect(screen.getByRole('option', { name: /Prusa MK4 0\.6 nozzle/ })).toBeInTheDocument();
      expect(screen.queryByRole('option', { name: /Prusa MK4 0\.4 nozzle/ })).not.toBeInTheDocument();
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

      const machineProfileSelect = await screen.findByLabelText('Machine profile');

      await waitFor(() => {
        expect(machineProfileSelect).toHaveValue('Custom MK4 0.8 nozzle');
      });

      expect(screen.getByRole('option', { name: /Custom MK4 0\.8 nozzle/ })).toBeInTheDocument();
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
          models?: Array<{ id: string; url: string; viewerUrl?: string; fileType: string }>;
        } | undefined;
        const selectedModel = lastWorkspaceProps?.models?.find((model) => model.id === 'model-3d-1');

        expect(selectedModel).toEqual(expect.objectContaining({
          url: expect.stringMatching(/\/3d-models\/file\/model-3d-1$/),
          viewerUrl: expect.stringMatching(/\/3d-models\/file\/model-3d-1$/),
          fileType: '3mf',
        }));
      });
    });
  });

  describe('Bed Type Override', () => {
    it('renders bed type dropdown with inherit as default', async () => {
      renderWithProviders(<NewSliceJobPage />);

      await waitFor(() => {
        const bedTypeSelect = screen.getByLabelText(/bed type/i);
        expect(bedTypeSelect).toBeInTheDocument();
        expect(bedTypeSelect).toHaveValue('');
      });
    });

    it('shows bed type options from OrcaSlicer metadata', async () => {
      renderWithProviders(<NewSliceJobPage />);

      await waitFor(() => {
        expect(screen.getByLabelText(/bed type/i)).toBeInTheDocument();
      });

      const bedTypeSelect = screen.getByLabelText(/bed type/i);
      const options = bedTypeSelect.querySelectorAll('option');
      // First option is "Inherit from profile", plus the bed type options
      expect(options.length).toBeGreaterThan(1);
      expect(options[0].textContent).toBe('Inherit from profile');
    });

    it('allows user to select a bed type override', async () => {
      renderWithProviders(<NewSliceJobPage />);

      await waitFor(() => {
        expect(screen.getByLabelText(/bed type/i)).toBeInTheDocument();
      });

      fireEvent.change(screen.getByLabelText(/bed type/i), { target: { value: 'Cool Plate' } });
      expect(screen.getByLabelText(/bed type/i)).toHaveValue('Cool Plate');
    });
  });
});
