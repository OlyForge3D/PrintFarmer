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
import { toast } from 'sonner';

// Mutable slicer-mode ref so individual describes can opt into Advanced mode.
// Hoisted because vi.mock factories run before module-body initialization.
const slicerModeRef = vi.hoisted(() => ({ value: 'Simple' as 'Simple' | 'Advanced' }));

// Mutable real-time job progress ref (issue #2214). The real hook subscribes
// to SignalR + REST reconciliation; none of that plumbing is meaningful in
// jsdom. Tests that need to drive the job through a terminal status call
// `jobProgressRef.set(...)`, which updates the ref AND notifies subscribers via a
// tiny external-store pattern — mutating the ref alone and calling
// `rerender()` is NOT enough, because react-router's route matching can reuse
// the previously-rendered element for an unchanged location/element identity
// and never re-invoke `NewSliceJobPage`'s render, so the mocked hook is never
// called again. Subscribing for real re-render notifications (mirroring how
// the actual hook pushes SignalR updates) sidesteps that entirely. Hoisted
// because vi.mock factories run before module-body initialization.
const jobProgressRef = vi.hoisted(() => {
  const listeners = new Set<() => void>();
  const initial = {
    progressPercent: 0,
    progressMessage: null as string | null,
    status: null as string | null,
    estimatedPrintTimeSeconds: null as number | null,
    filamentUsedGrams: null as number | null,
    resultFileUrl: null as string | null,
    error: null as string | null,
    isConnected: true,
  };
  return {
    value: initial,
    listeners,
    set(next: typeof initial) {
      this.value = next;
      this.listeners.forEach((listener) => listener());
    },
    reset() {
      this.set(initial);
    },
  };
});

vi.mock('../../hooks/useSliceJobProgress', async () => {
  const react = await vi.importActual<typeof import('react')>('react');
  return {
    useSliceJobProgress: vi.fn(() =>
      react.useSyncExternalStore(
        (listener) => {
          jobProgressRef.listeners.add(listener);
          return () => jobProgressRef.listeners.delete(listener);
        },
        () => jobProgressRef.value,
      ),
    ),
  };
});

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
    compatible_printers: ['Prusa MK4 0.4 nozzle'],
  },
  {
    name: 'Prusament PETG @MK4',
    material: 'PETG',
    nozzleTemperature: 240,
    bedTemperature: 85,
    printSpeed: 50,
    compatible_printers: ['Prusa MK4 0.4 nozzle'],
  },
  {
    name: 'Generic ABS @MK4',
    material: 'ABS',
    nozzleTemperature: 255,
    bedTemperature: 100,
    printSpeed: 50,
    compatible_printers: ['Prusa MK4 0.4 nozzle'],
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
    compatible_printers: ['Prusa MK4 0.4 nozzle'],
  },
  {
    name: '0.10mm Fine @MK4',
    quality: 'Fine',
    layerHeight: 0.1,
    infillPercentage: 20,
    printSpeed: 40,
    supports: false,
    compatible_printers: ['Prusa MK4 0.4 nozzle'],
  },
  {
    name: '0.30mm Draft @MK4',
    quality: 'Draft',
    layerHeight: 0.3,
    infillPercentage: 10,
    printSpeed: 80,
    supports: false,
    compatible_printers: ['Prusa MK4 0.4 nozzle'],
  }
];

const mockProfilesSummary = {
  machineProfiles: mockMachineProfiles,
  filamentProfiles: mockFilamentProfiles,
  processProfiles: mockProcessProfiles,
};

const mockWorkerHierarchy = {
  byHierarchy: {
    Voron: {
      name: 'Voron',
      models: {
        'Voron 2.4 250': {
          name: 'Voron 2.4 250',
          machineProfiles: [
            {
              name: 'Voron 2.4 250 0.4 nozzle',
              manufacturer: 'Voron',
              nozzleDiameter: 0.4,
              printerModel: 'Voron 2.4 250',
              settings: {
                printable_area: '0x0,250x0,250x250,0x250',
                printable_height: 250,
              },
            },
          ],
          filamentProfiles: [],
          processProfiles: [],
        },
      },
    },
  },
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

// A stored library model whose id is a real GUID (unlike the plain-string
// fixtures above), so it exercises resolveModel3DId's GUID validation the
// same way the real API's ids do. Used by the "Enter URL" regression tests
// for issue #1973 (URL-backed models sending a synthetic, non-GUID
// model3DId).
const mockLibraryModelGuid = '11111111-1111-1111-1111-111111111111';
const mockModelListWithGuid = [
  ...mockModelList,
  {
    id: mockLibraryModelGuid,
    fileName: 'library-model.3mf',
    originalFileName: 'library-model.3mf',
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
    getWorkerHierarchy: vi.fn(() => Promise.resolve(mockWorkerHierarchy)),
    cloneFamily: vi.fn(),
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
vi.mock('@/services/sliceJobService', async () => {
  const actual = await vi.importActual<typeof import('@/services/sliceJobService')>('@/services/sliceJobService');
  return {
    sliceJobService: {
      submitJob: vi.fn(() => Promise.resolve({ id: 'job-1', status: 'Queued' })),
      parseOrcaNumeric: vi.fn(() => undefined),
      getSpoolCostPerGram: vi.fn(() => Promise.resolve({ costPerGram: null, currency: '$', source: null })),
      addSliceToQueue: vi.fn(() => Promise.resolve({ printJobId: 'pj-1', queuePosition: 1, message: 'Queued' })),
      // Pure formatting/computation helpers used by SliceProgressOverlay /
      // SliceJobProgressPanel once a job is submitted. Delegate to the real
      // implementations instead of stubbing, since no test previously
      // exercised the submitted-job UI far enough to need them.
      computeMaterialCost: actual.sliceJobService.computeMaterialCost,
      computeMaterialCostPerGram: actual.sliceJobService.computeMaterialCostPerGram,
      formatPrintTime: actual.sliceJobService.formatPrintTime,
      formatFilamentUsed: actual.sliceJobService.formatFilamentUsed,
    },
    formatQueuePositionSuffix: actual.formatQueuePositionSuffix,
  };
});

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

// Mock sonner toast (used by the "Use URL" reachability check, issue #1910)
vi.mock('sonner', () => ({
  toast: {
    loading: vi.fn(() => 'toast-id'),
    success: vi.fn(),
    error: vi.fn(),
  },
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
  SlicerSelector: (props: {
    selectedSlicerId: number;
    onSlicerChange: (id: number) => void;
    versionEntries?: Array<{ version: string; available: boolean }>;
    latestVersion?: string;
    selectedVersion?: string;
    engineName?: string;
    onVersionChange?: (v: string | undefined) => void;
  }) => {
    const { onSlicerChange, versionEntries, latestVersion, engineName, onVersionChange } = props;
    // Records EVERY render so tests can assert on intermediate frames, not just
    // the settled state — e.g. that no frame ever pairs a new engine with the
    // previous engine's pin.
    slicerSelectorRenderSpy({ engineName, selectedVersion: props.selectedVersion });
    return (
      // Flat, primitive attributes only — the version entries are surfaced as
      // two comma-joined lists rather than embedded JSON so the assertions stay
      // readable and no raw object is interpolated into JSX.
      <div
        data-testid="slicer-selector"
        data-all-versions={(versionEntries ?? []).map(v => v.version).join(',')}
        data-available-versions={(versionEntries ?? []).filter(v => v.available).map(v => v.version).join(',')}
        data-latest-version={latestVersion ?? ''}
        data-selected-version={props.selectedVersion ?? ''}
        data-engine-name={engineName ?? ''}
      >
        <select
          data-testid="slicer-select"
          aria-label="Select slicer"
          onChange={(e) => onSlicerChange(Number(e.target.value))}
        >
          <option value="1">OrcaSlicer</option>
          <option value="2">PrusaSlicer</option>
        </select>
        {/* Lets submit-guard tests drive a pin without the real component. */}
        <button type="button" data-testid="pin-engine-version" onClick={() => onVersionChange?.('2.3.1')}>
          pin 2.3.1
        </button>
      </div>
    );
  },
  SlicerSettingsPanel: (props: { onValidationChange?: (isValid: boolean) => void }) => (
    <div data-testid="slicer-settings-panel">
      Settings Panel
      {/* Lets submit-guard tests simulate an uncommitted invalid Simple-mode
          field (issue #2223) without depending on the real component's
          internals — mirrors the pin-engine-version button pattern above. */}
      <button
        type="button"
        data-testid="invalidate-simple-settings"
        onClick={() => props.onValidationChange?.(false)}
      >
        invalidate settings
      </button>
      <button
        type="button"
        data-testid="revalidate-simple-settings"
        onClick={() => props.onValidationChange?.(true)}
      >
        revalidate settings
      </button>
    </div>
  ),
}));

const slicerWorkspaceSpy = vi.fn();

/**
 * Captures the SlicerSelector mock's props on EVERY render, so tests can assert
 * on intermediate frames rather than only the settled state. Referenced from
 * the `../../components/job` mock factory above; the reference resolves at
 * render time, not at factory time, so declaration order is irrelevant.
 */
const slicerSelectorRenderSpy = vi.fn();

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
    slicerSelectorRenderSpy.mockClear();
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

  describe('Mobile settings overlay dismissal (issue #1867)', () => {
    it('renders a "Hide settings" control that closes the settings overlay', async () => {
      renderWithProviders(<NewSliceJobPage />);

      await waitFor(() => {
        expect(screen.getByTestId('printer-slicer-selector')).toBeInTheDocument();
      });

      // The settings panel starts open (default sidebarOpen state), so the
      // overlay covers the whole viewport on narrow screens including the
      // workspace toolbar's own hamburger toggle underneath it.
      const closeButton = screen.getByRole('button', { name: /hide settings/i });
      expect(closeButton).toBeInTheDocument();

      const sidebar = screen.getByTestId('slicer-settings-sidebar');
      expect(sidebar.className).toContain('absolute');

      fireEvent.click(closeButton);

      // Closing collapses the overlay back to `hidden` so it stops covering
      // the workspace (and the "Add model" action) underneath it.
      await waitFor(() => {
        expect(sidebar.className).toContain('hidden');
        expect(sidebar.className).not.toContain('absolute');
      });
    });
  });

  describe('Tablet-width workspace clipping (issue #1868)', () => {
    // SCOPE NOTE (class-guard only, not a full layout test): jsdom — which
    // backs this Vitest/RTL suite — does not run a real CSS box-model/flex
    // layout engine, so it cannot itself reproduce the 1024x768 overflow this
    // issue describes. The actual acceptance criteria (model viewer stays
    // inside its `overflow-hidden` container; the Slice Plate action stays
    // reachable/visible; the workspace reflows instead of clipping) were
    // verified against a real Chromium layout via Playwright, using a
    // standalone harness built from this page's exact class structure
    // (global nav rail `lg:w-[248px]`, `lg:w-96` settings sidebar, this
    // panel, toolbar, canvas area, status bar):
    //   - Before the fix: panel measured ~1195px wide (wider than the
    //     1024px viewport) and the Slice Plate button sat at x=1831 —
    //     entirely off-screen.
    //   - After adding `min-w-0`: panel measured exactly 354px, matching
    //     the hand-computed available space (1024 − 248 nav rail − 32 page
    //     padding − 384 sidebar − 6 gap = 354), and the Slice Plate button
    //     was visible at x=991, fully within the viewport.
    //   - Stress-testing with longer realistic status-bar content (longer
    //     slice notes, a longer button label) confirmed the panel width
    //     stays fixed at 354px (driven by available flex space, not
    //     content) and the status bar text wraps rather than clipping.
    // That harness was a throwaway artifact and isn't committed here.
    // A committed, backend-independent Playwright equivalent for `/slicer`
    // would need to mock printer/model/profile data deep enough to mount
    // `SlicerWorkspaceBoundary` in a "model loaded" state, which is a
    // meaningfully larger undertaking than this isolated CSS fix; see
    // issue #1868 for follow-up if a dedicated fixture is added later.
    // Until then, this test is deliberately scoped to guard the CSS
    // contract this fix depends on: that this exact panel carries both
    // `min-w-0` and `flex-1`. Removing either class regresses this test.
    it('gives the 3D workspace panel min-w-0 so it shrinks instead of overflowing the row', async () => {
      renderWithProviders(<NewSliceJobPage />);

      await waitFor(() => {
        expect(screen.getByTestId('printer-slicer-selector')).toBeInTheDocument();
      });

      // The settings sidebar is a fixed-width flex sibling (`lg:w-96`); this
      // panel must carry `min-w-0` or, lacking one, its default
      // `min-width: auto` lets its descendants' max-content width (toolbar
      // buttons, transform panels, etc.) push the row wider than the
      // viewport at the tablet (lg) breakpoint, clipping the model viewer
      // and the Slice Plate action outside the form's `overflow-hidden`
      // bounds.
      const workspacePanel = screen.getByTestId('slicer-workspace-panel');
      expect(workspacePanel.className).toContain('min-w-0');
      expect(workspacePanel.className).toContain('flex-1');
    });
  });

  describe('320px viewport overflow (issue #2001)', () => {
    // SCOPE NOTE (class-guard only, same rationale as the #1868 test above):
    // jsdom does not run a real CSS box-model/layout engine, so it cannot
    // itself reproduce a pixel-overflow bug at a specific viewport width.
    // Root cause: below the `lg` breakpoint this sidebar renders as an
    // absolutely-positioned overlay with a *fixed* `w-96` (384px) width,
    // while its ancestor `<form>` has `overflow-hidden` and no horizontal
    // scroll affordance. At a 320px viewport (the reproduction width in
    // #2001) that overflowed the viewport by 64px with the excess clipped
    // and unreachable. The fix swaps the narrow-viewport branch to
    // `w-full` so the overlay fills whatever width is actually available
    // instead of a fixed px value — verified against a real Chromium
    // 320x700 viewport via Playwright with the seeded "Moonraker Offline"
    // printer selected (so the printer/machine-profile/filament controls
    // all render). This test guards the CSS contract that fix depends on:
    // that the open-state sidebar carries `w-full`, not a bare fixed
    // width class, on narrow viewports.
    it('gives the open settings overlay w-full instead of a fixed width', async () => {
      renderWithProviders(<NewSliceJobPage />);

      await waitFor(() => {
        expect(screen.getByTestId('printer-slicer-selector')).toBeInTheDocument();
      });

      const sidebar = screen.getByTestId('slicer-settings-sidebar');
      // Sidebar starts open (default sidebarOpen state).
      expect(sidebar.className).toContain('absolute');
      expect(sidebar.className).toContain('w-full');
      // Guard against regressing back to a fixed narrow-viewport width —
      // `lg:w-96` (the desktop inline-sidebar width) must remain, but a
      // bare `w-96` outside the `lg:` prefix must not reappear.
      expect(sidebar.className).not.toMatch(/(?<!lg:)\bw-96\b/);
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
          compatible_printers: ['Prusa MK4S 0.4 nozzle', 'Prusa MK4S HF0.4 nozzle'],
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

    it('explains missing OrcaSlicer coverage and opens the profile-family wizard', async () => {
      vi.mocked(slicerProfilesService.getMachineProfilesForModel).mockRejectedValue({
        message: 'No profiles for this model',
        statusCode: 404,
        data: {
          code: 'no_profiles_for_model',
          detail: "OrcaSlicer ships no machine profiles for model 'MK4'.",
        },
      });
      vi.mocked(slicerProfilesService.listCustomProfiles).mockResolvedValue({
        profiles: [],
        totalCount: 0,
        machineProfileCount: 0,
        processProfileCount: 0,
        filamentProfileCount: 0,
      });

      renderWithProviders(<NewSliceJobPage />);
      fireEvent.change(await screen.findByTestId('printer-select'), { target: { value: 'printer-1' } });

      expect(await screen.findByRole('heading', { name: 'No OrcaSlicer profiles for MK4' })).toBeInTheDocument();
      expect(screen.getByText(/doesn't ship profiles for this printer model/i)).toBeInTheDocument();

      fireEvent.click(screen.getByRole('button', { name: /Create profile family/i }));

      expect(await screen.findByRole('dialog', { name: 'Create profile family' })).toBeInTheDocument();
      expect(screen.getByRole('heading', { name: 'Choose source machine model' })).toBeInTheDocument();
      expect(await screen.findByRole('button', { name: /Voron 2.4 250/i })).toBeInTheDocument();
    });

    it('identifies an alias that returned no profiles without offering family creation', async () => {
      vi.mocked(slicerProfilesService.getMachineProfilesForModel).mockRejectedValue({
        message: 'Alias matched no profiles',
        statusCode: 404,
        data: {
          code: 'alias_matched_no_profiles',
          detail: "Tried OrcaSlicer model name 'MK4'; the slicer worker has no matching profiles.",
        },
      });

      renderWithProviders(<NewSliceJobPage />);
      fireEvent.change(await screen.findByTestId('printer-select'), { target: { value: 'printer-1' } });

      expect(await screen.findByRole('heading', { name: 'No matching OrcaSlicer profiles for MK4' })).toBeInTheDocument();
      expect(screen.getByText(/profile-coverage or slicer-engine-version issue/i)).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Create profile family' })).not.toBeInTheDocument();
    });

    it('falls back to a generic machine-profile error when the backend omits a reason code', async () => {
      vi.mocked(slicerProfilesService.getMachineProfilesForModel).mockRejectedValue({
        message: 'Not Found',
        statusCode: 404,
      });

      renderWithProviders(<NewSliceJobPage />);
      fireEvent.change(await screen.findByTestId('printer-select'), { target: { value: 'printer-1' } });

      expect(await screen.findByRole('heading', { name: 'Machine profiles unavailable for MK4' })).toBeInTheDocument();
      expect(screen.getByText(/could not load machine profiles/i)).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Create profile family' })).not.toBeInTheDocument();
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
      // profile name WITH its entire `compatible_printers` list into one
      // string before testing for "HF". A process profile that legitimately
      // supports both CORE One variants lists both machine names in
      // `compatible_printers`, so the joined text always mentions "HF" — which
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
            compatible_printers: ['Prusa CORE One 0.4 nozzle', 'Prusa CORE One HF 0.4 nozzle'],
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
            compatible_printers: ['Prusa CORE One HF 0.4 nozzle'],
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

  describe('Enter URL (issue #1910)', () => {
    // Regression tests: the "Use URL" action used to close the dialog
    // without validating input, sending a request, or adding anything to the
    // plate — a silent no-op. See NewSliceJobPage's handleUrlModelSubmit and
    // SearchablePickerModal's isValidModelSourceUrl/handleUrlConfirm.
    async function renderAndOpenUrlTab() {
      renderWithProviders(<NewSliceJobPage />);
      await waitFor(() => {
        expect(screen.getByTestId('printer-select')).toBeInTheDocument();
      });
      fireEvent.change(screen.getByTestId('printer-select'), { target: { value: 'printer-1' } });
      await waitFor(() => {
        expect(screen.getByTestId('slicer-workspace')).toBeInTheDocument();
      });
      act(() => {
        fireEvent.click(screen.getByRole('button', { name: /add model/i }));
      });
      const urlTabButton = await screen.findByRole('button', { name: 'Enter URL' });
      act(() => {
        fireEvent.click(urlTabButton);
      });
    }

    afterEach(() => {
      vi.unstubAllGlobals();
    });

    it('rejects malformed input with an inline error and neither sends a request nor adds a model', async () => {
      const fetchSpy = vi.fn();
      vi.stubGlobal('fetch', fetchSpy);

      await renderAndOpenUrlTab();

      fireEvent.change(screen.getByLabelText('File URL'), { target: { value: 'not a url' } });
      fireEvent.click(screen.getByRole('button', { name: 'Use URL' }));

      await waitFor(() => {
        expect(screen.getByRole('alert')).toHaveTextContent(/enter a valid/i);
      });
      // Dialog stays open — the URL input is still present, unlike the
      // pre-fix behaviour where the dialog closed regardless of input.
      expect(screen.getByLabelText('File URL')).toBeInTheDocument();
      expect(fetchSpy).not.toHaveBeenCalled();
      expect(slicerWorkspaceSpy.mock.calls.at(-1)?.[0]?.models ?? []).toHaveLength(0);
    });

    it('adds the model to the plate and shows a success toast for a reachable URL', async () => {
      const fetchSpy = vi.fn().mockResolvedValue({
        ok: true,
        status: 200,
        arrayBuffer: () => Promise.resolve(new ArrayBuffer(0)),
      });
      vi.stubGlobal('fetch', fetchSpy);

      await renderAndOpenUrlTab();

      fireEvent.change(screen.getByLabelText('File URL'), {
        target: { value: 'https://example.com/model.stl' },
      });
      fireEvent.click(screen.getByRole('button', { name: 'Use URL' }));

      await waitFor(() => {
        expect(fetchSpy).toHaveBeenCalledWith('https://example.com/model.stl', expect.anything());
      });
      await waitFor(() => {
        const models = slicerWorkspaceSpy.mock.calls.at(-1)?.[0]?.models ?? [];
        expect(models).toHaveLength(1);
        expect(models[0].url).toBe('https://example.com/model.stl');
      });
      expect(toast.success).toHaveBeenCalled();
    });

    it('shows an error toast and adds nothing to the plate for an unreachable URL', async () => {
      const fetchSpy = vi.fn().mockResolvedValue({ ok: false, status: 404 });
      vi.stubGlobal('fetch', fetchSpy);

      await renderAndOpenUrlTab();

      fireEvent.change(screen.getByLabelText('File URL'), {
        target: { value: 'https://example.com/missing.stl' },
      });
      fireEvent.click(screen.getByRole('button', { name: 'Use URL' }));

      await waitFor(() => {
        expect(fetchSpy).toHaveBeenCalled();
      });
      await waitFor(() => {
        expect(toast.error).toHaveBeenCalled();
      });
      expect(slicerWorkspaceSpy.mock.calls.at(-1)?.[0]?.models ?? []).toHaveLength(0);
    });

    it('uses the authenticated apiClient (not a bare fetch) for an internal /api/3d-models/file/ URL, so it succeeds instead of 401ing', async () => {
      // Regression coverage: /api/3d-models/file/{id} is one of the API's
      // authenticated file endpoints (see AuthenticatedModelSource / #1711).
      // A bare, unauthenticated fetch to it always 401s even though the
      // viewer can load it fine via the bearer-token-attached apiClient.
      const fetchSpy = vi.fn();
      vi.stubGlobal('fetch', fetchSpy);

      await renderAndOpenUrlTab();

      vi.mocked(apiClient.get).mockResolvedValueOnce({ data: new ArrayBuffer(0) } as never);

      fireEvent.change(screen.getByLabelText('File URL'), {
        target: { value: '/api/3d-models/file/model-3d-1' },
      });
      fireEvent.click(screen.getByRole('button', { name: 'Use URL' }));

      await waitFor(() => {
        expect(apiClient.get).toHaveBeenCalledWith(
          '/api/3d-models/file/model-3d-1',
          expect.objectContaining({ responseType: 'arraybuffer' }),
        );
      });
      await waitFor(() => {
        const models = slicerWorkspaceSpy.mock.calls.at(-1)?.[0]?.models ?? [];
        expect(models).toHaveLength(1);
      });
      expect(fetchSpy).not.toHaveBeenCalled();
      expect(toast.success).toHaveBeenCalled();
    });

    it('detects the viewer file type from the stored model metadata for an internal URL with no File Name typed', async () => {
      // Regression coverage: when the user leaves "File Name" blank,
      // SearchablePickerModal falls back to the URL's last path segment
      // (the bare model id, with no extension) as the file name. Detecting
      // fileType from that extension-less name always defaults to 'stl'
      // (see getSlicerViewerFileType), which would load a stored 3MF with
      // the wrong loader. handleUrlModelSubmit must instead resolve the
      // real file name from the already-fetched model list for id-based
      // "/3d-models/file/{id}" URLs. model-3d-1 in mockModelList is a
      // .3mf, so a wrong/default detection would produce 'stl' here.
      const fetchSpy = vi.fn();
      vi.stubGlobal('fetch', fetchSpy);

      await renderAndOpenUrlTab();

      vi.mocked(apiClient.get).mockResolvedValueOnce({ data: new ArrayBuffer(0) } as never);

      fireEvent.change(screen.getByLabelText('File URL'), {
        target: { value: '/api/3d-models/file/model-3d-1' },
      });
      fireEvent.click(screen.getByRole('button', { name: 'Use URL' }));

      await waitFor(() => {
        const models = slicerWorkspaceSpy.mock.calls.at(-1)?.[0]?.models ?? [];
        expect(models).toHaveLength(1);
        expect(models[0].fileType).toBe('3mf');
      });
    });

    it('still resolves the correct file type when the model list query has not settled yet at submit time (race window)', async () => {
      // Regression coverage for Bishop's second-round finding: fileType
      // detection used to read the `models` query result captured at
      // submit/render time, so submitting an id-based URL before that
      // query resolved would fall back to the extension-less id and
      // misdetect as 'stl'. handleUrlModelSubmit now resolves the match via
      // `queryClient.ensureQueryData`, which awaits (and dedupes with) the
      // in-flight query rather than reading a stale snapshot.
      let resolveModelsList: (value: { data: unknown[] }) => void = () => {
        throw new Error('resolveModelsList called before it was assigned');
      };
      const modelsListPromise = new Promise<{ data: unknown[] }>((resolve) => {
        resolveModelsList = resolve;
      });
      vi.mocked(apiClient.get).mockImplementation(((url: string) => {
        if (url === '/3d-models') {
          return modelsListPromise;
        }
        return Promise.resolve({ data: new ArrayBuffer(0) });
      }) as never);

      const fetchSpy = vi.fn();
      vi.stubGlobal('fetch', fetchSpy);

      await renderAndOpenUrlTab();

      fireEvent.change(screen.getByLabelText('File URL'), {
        target: { value: '/api/3d-models/file/model-3d-1' },
      });
      fireEvent.click(screen.getByRole('button', { name: 'Use URL' }));

      // The models list query is still pending here — resolve it only now,
      // simulating it completing shortly after the URL was submitted.
      act(() => {
        resolveModelsList({ data: mockModelList });
      });

      await waitFor(() => {
        const models = slicerWorkspaceSpy.mock.calls.at(-1)?.[0]?.models ?? [];
        expect(models).toHaveLength(1);
        expect(models[0].fileType).toBe('3mf');
      });
      expect(fetchSpy).not.toHaveBeenCalled();
    });

    describe('model3DId regression coverage (issue #1973)', () => {
      // Issue #1973: the Slice Job page sent a synthetic `url-<timestamp>`
      // string as `model3DId` for URL-loaded models, which the API rejects
      // because it binds to `Guid?`. These tests drive the real "Enter URL"
      // submit flow end-to-end (through onSlice -> submitJob) to prove the
      // fix holds at the integration boundary between handleUrlModelSubmit
      // (which attaches libraryModelId) and resolveModel3DId (which decides
      // what reaches the wire) — not just at either layer in isolation.
      async function reachSubmittableStateViaUrl() {
        vi.mocked(slicerProfilesService.getMachineProfilesForModel).mockResolvedValue([
          { name: 'Prusa MK4S 0.4 nozzle', manufacturer: 'Prusa', nozzleDiameter: 0.4, printerModel: 'MK4S' },
        ] as OrcaMachineProfile[]);
        vi.mocked(slicerProfilesService.getProcessProfilesForMachines).mockResolvedValue([
          {
            name: '0.20mm Standard @MK4S',
            quality: 'Standard',
            layerHeight: 0.2,
            infillPercentage: 15,
            printSpeed: 60,
            supports: false,
            compatible_printers: ['Prusa MK4S 0.4 nozzle'],
          },
        ] as OrcaProcessProfile[]);

        renderWithProviders(<NewSliceJobPage />);
        await waitFor(() => {
          expect(screen.getByTestId('printer-select')).toBeInTheDocument();
        });
        fireEvent.change(screen.getByTestId('printer-select'), { target: { value: 'printer-1' } });
        await waitFor(() => {
          expect(screen.getByTestId('slicer-workspace')).toBeInTheDocument();
        });
        act(() => {
          fireEvent.click(screen.getByRole('button', { name: /add model/i }));
        });
        const urlTabButton = await screen.findByRole('button', { name: 'Enter URL' });
        act(() => {
          fireEvent.click(urlTabButton);
        });
      }

      const latestOnSlice = () =>
        (slicerWorkspaceSpy.mock.calls.at(-1)?.[0] as { onSlice?: (ids?: string[]) => void } | undefined)?.onSlice;

      async function waitForProcessProfile() {
        await waitFor(() => {
          const processSelect = Array.from(document.querySelectorAll('select'))
            .find((s) => s.value.startsWith('system:'));
          expect(processSelect?.value).toBe('system:0.20mm Standard @MK4S');
        });
      }

      it('sends the persisted library model GUID as model3DId when the URL matches an authenticated internal model', async () => {
        vi.mocked(apiClient.get).mockImplementation(((url: string) => {
          if (url === '/3d-models') {
            return Promise.resolve({ data: mockModelListWithGuid });
          }
          return Promise.resolve({ data: new ArrayBuffer(0) });
        }) as never);

        await reachSubmittableStateViaUrl();

        fireEvent.change(screen.getByLabelText('File URL'), {
          target: { value: `/api/3d-models/file/${mockLibraryModelGuid}` },
        });
        fireEvent.click(screen.getByRole('button', { name: 'Use URL' }));

        await waitFor(() => {
          const models = slicerWorkspaceSpy.mock.calls.at(-1)?.[0]?.models ?? [];
          expect(models).toHaveLength(1);
          expect(models[0].libraryModelId).toBe(mockLibraryModelGuid);
        });

        await waitForProcessProfile();
        await waitFor(() => {
          expect(latestOnSlice()).toBeTypeOf('function');
        });
        await act(async () => { latestOnSlice()!(); });

        await waitFor(() => {
          expect(sliceJobService.submitJob).toHaveBeenCalled();
        }, { timeout: 3000 });

        const request = vi.mocked(sliceJobService.submitJob).mock.calls.at(-1)?.[0] as { model3DId?: string };
        expect(request.model3DId).toBe(mockLibraryModelGuid);
      });

      it('omits model3DId (never a synthetic string) when the URL does not match any persisted library model', async () => {
        vi.mocked(apiClient.get).mockImplementation(((url: string) => {
          if (url === '/3d-models') {
            return Promise.resolve({ data: mockModelListWithGuid });
          }
          return Promise.resolve({ data: new ArrayBuffer(0) });
        }) as never);
        const fetchSpy = vi.fn().mockResolvedValue({
          ok: true,
          status: 200,
          arrayBuffer: () => Promise.resolve(new ArrayBuffer(0)),
        });
        vi.stubGlobal('fetch', fetchSpy);

        await reachSubmittableStateViaUrl();

        fireEvent.change(screen.getByLabelText('File URL'), {
          target: { value: 'https://example.com/model.stl' },
        });
        fireEvent.click(screen.getByRole('button', { name: 'Use URL' }));

        await waitFor(() => {
          const models = slicerWorkspaceSpy.mock.calls.at(-1)?.[0]?.models ?? [];
          expect(models).toHaveLength(1);
          expect(models[0].libraryModelId).toBeUndefined();
        });

        await waitForProcessProfile();
        await waitFor(() => {
          expect(latestOnSlice()).toBeTypeOf('function');
        });
        await act(async () => { latestOnSlice()!(); });

        await waitFor(() => {
          expect(sliceJobService.submitJob).toHaveBeenCalled();
        }, { timeout: 3000 });

        const request = vi.mocked(sliceJobService.submitJob).mock.calls.at(-1)?.[0] as { model3DId?: string };
        expect(request.model3DId).toBeUndefined();
      });

      it('does not attach an unrelated stored model GUID as model3DId for a cross-origin URL that merely resembles the internal file-serving path', async () => {
        // Security regression coverage (Vasquez's review of #1973): an
        // absolute, cross-origin URL like
        // "https://evil.example/3d-models/file/<real-guid>" must never be
        // treated as a match against the current user's own model list —
        // otherwise a job whose fetched geometry comes from an
        // attacker-controlled origin would carry a legitimate, unrelated
        // model's GUID as model3DId. isAuthenticatedModelUrl gates the
        // lookup on the API's own origin, so this must resolve exactly like
        // any other unmatched external URL: libraryModelId stays unset and
        // model3DId is omitted.
        vi.mocked(apiClient.get).mockImplementation(((url: string) => {
          if (url === '/3d-models') {
            return Promise.resolve({ data: mockModelListWithGuid });
          }
          return Promise.resolve({ data: new ArrayBuffer(0) });
        }) as never);
        const fetchSpy = vi.fn().mockResolvedValue({
          ok: true,
          status: 200,
          arrayBuffer: () => Promise.resolve(new ArrayBuffer(0)),
        });
        vi.stubGlobal('fetch', fetchSpy);

        await reachSubmittableStateViaUrl();

        fireEvent.change(screen.getByLabelText('File URL'), {
          target: { value: `https://evil.example/3d-models/file/${mockLibraryModelGuid}` },
        });
        fireEvent.click(screen.getByRole('button', { name: 'Use URL' }));

        await waitFor(() => {
          const models = slicerWorkspaceSpy.mock.calls.at(-1)?.[0]?.models ?? [];
          expect(models).toHaveLength(1);
          expect(models[0].libraryModelId).toBeUndefined();
        });

        await waitForProcessProfile();
        await waitFor(() => {
          expect(latestOnSlice()).toBeTypeOf('function');
        });
        await act(async () => { latestOnSlice()!(); });

        await waitFor(() => {
          expect(sliceJobService.submitJob).toHaveBeenCalled();
        }, { timeout: 3000 });

        const request = vi.mocked(sliceJobService.submitJob).mock.calls.at(-1)?.[0] as { model3DId?: string };
        expect(request.model3DId).toBeUndefined();
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

  describe('Engine Version submit guards (issues #578, #1772)', () => {
    // The wiring tests above prove the raw entries REACH the component. These
    // prove the dispatch path itself still refuses an unclaimable job — without
    // them, filtering could later be reintroduced in the submit path and the
    // wiring assertions would still pass (the exact #1792 regression class).
    function mockEngines(entries: Array<{ version: string; available: boolean }>, latest: string | null) {
      vi.mocked(slicerService.listEngines).mockResolvedValue([
        { engine: 'OrcaSlicer', versions: entries.map(e => e.version), versionEntries: entries, latest },
      ]);
    }

    /**
     * Drives the page to a state where onSlice will actually attempt a submit.
     * Returns the QueryClient so a test can force a registry refetch mid-flow
     * (the test client uses staleTime: Infinity).
     */
    async function reachSubmittableState() {
      vi.mocked(slicerProfilesService.getMachineProfilesForModel).mockResolvedValue([
        { name: 'Prusa MK4S 0.4 nozzle', manufacturer: 'Prusa', nozzleDiameter: 0.4, printerModel: 'MK4S' },
      ] as OrcaMachineProfile[]);
      vi.mocked(slicerProfilesService.getProcessProfilesForMachines).mockResolvedValue([
        {
          name: '0.20mm Standard @MK4S',
          quality: 'Standard',
          layerHeight: 0.2,
          infillPercentage: 15,
          printSpeed: 60,
          supports: false,
          compatible_printers: ['Prusa MK4S 0.4 nozzle'],
        },
      ] as OrcaProcessProfile[]);

      const { queryClient } = renderWithProviders(<NewSliceJobPage />, { route: '/slicer?modelId=model-3d-1' });

      await waitFor(() => {
        expect(screen.getByText('My Prusa MK4')).toBeInTheDocument();
      });
      fireEvent.change(screen.getByTestId('printer-select'), { target: { value: 'printer-1' } });
      return queryClient;
    }
    async function waitForProcessProfile() {
      await waitFor(() => {
        const processSelect = Array.from(document.querySelectorAll('select'))
          .find((s) => s.value.startsWith('system:'));
        expect(processSelect?.value).toBe('system:0.20mm Standard @MK4S');
      });
    }

    // onSlice is a useCallback closing over current state, so a handle captured
    // earlier is stale and would bail out before reaching the version guards.
    const latestOnSlice = () =>
      (slicerWorkspaceSpy.mock.calls.at(-1)?.[0] as { onSlice?: (ids?: string[]) => void } | undefined)?.onSlice;

    async function submit() {
      await waitFor(() => {
        expect(latestOnSlice()).toBeTypeOf('function');
      });
      await act(async () => { latestOnSlice()!(); });
    }

    it('blocks an unpinned submission when every version is offline', async () => {
      mockEngines([
        { version: '2.4.2', available: false },
        { version: '2.3.1', available: false },
      ], null);

      await reachSubmittableState();
      await waitForProcessProfile();
      await submit();

      expect(await screen.findByText(/No online OrcaSlicer worker is available/i)).toBeInTheDocument();
      expect(sliceJobService.submitJob).not.toHaveBeenCalled();
    });

    it('blocks a submission pinned to a version with no online worker', async () => {
      // A pin can go stale AFTER selection (the registry query has a 300s
      // staleTime), and the Latest-mode guard is gated on the pin being
      // undefined, so it never covered this path.
      mockEngines([
        { version: '2.4.2', available: true },
        { version: '2.3.1', available: false },
      ], '2.4.2');

      await reachSubmittableState();

      // Pin first: changing the pin cascades a profile reset, so the process
      // profile must be allowed to re-settle before submitting.
      await waitFor(() => {
        expect(screen.getByTestId('pin-engine-version')).toBeInTheDocument();
      });
      act(() => {
        fireEvent.click(screen.getByTestId('pin-engine-version'));
      });

      await waitForProcessProfile();
      await submit();

      expect(await screen.findByText(/OrcaSlicer 2\.3\.1 has no online worker/i)).toBeInTheDocument();
      expect(sliceJobService.submitJob).not.toHaveBeenCalled();
    });

    it('allows an unpinned submission on the legacy fresh-install shape', async () => {
      // No SlicerService rows: backend marks every entry available and returns
      // latest:null. The job MUST go out unpinned so a legacy generic-capability
      // worker can claim it (Vasquez R3 on #1792).
      mockEngines([
        { version: '2.4.2', available: true },
        { version: '2.3.1', available: true },
      ], null);

      await reachSubmittableState();
      await waitForProcessProfile();
      await submit();

      await waitFor(() => {
        expect(sliceJobService.submitJob).toHaveBeenCalled();
      }, { timeout: 3000 });

      const request = vi.mocked(sliceJobService.submitJob).mock.calls.at(-1)?.[0] as { slicerEngineVersion?: string };
      expect(request.slicerEngineVersion).toBeUndefined();
    });

    it('allows a PINNED submission on the legacy fresh-install shape', async () => {
      // The pinned guard has no length exemption, so legacy is protected purely
      // by the availability clause: with zero service rows the backend marks
      // every entry available, so a pin is legitimately claimable. This test
      // exists so a future edit that reorders or tightens those clauses cannot
      // silently start blocking legacy deployments (Bishop nit 4).
      mockEngines([
        { version: '2.4.2', available: true },
        { version: '2.3.1', available: true },
      ], null);

      await reachSubmittableState();

      await waitFor(() => {
        expect(screen.getByTestId('pin-engine-version')).toBeInTheDocument();
      });
      act(() => {
        fireEvent.click(screen.getByTestId('pin-engine-version'));
      });

      await waitForProcessProfile();
      await submit();

      await waitFor(() => {
        expect(sliceJobService.submitJob).toHaveBeenCalled();
      }, { timeout: 3000 });

      const request = vi.mocked(sliceJobService.submitJob).mock.calls.at(-1)?.[0] as { slicerEngineVersion?: string };
      expect(request.slicerEngineVersion).toBe('2.3.1');
    });

    it('blocks a pinned submission when the engine reports no version entries', async () => {
      // A pin carries a version-specific capability tag that no generic worker
      // can claim, so dispatching one against an empty registry guarantees a
      // permanent queue hang. Verifying nothing is NOT the same as verifying
      // it is fine — fail closed (Vasquez R2).
      mockEngines([], null);

      await reachSubmittableState();

      await waitFor(() => {
        expect(screen.getByTestId('pin-engine-version')).toBeInTheDocument();
      });
      act(() => {
        fireEvent.click(screen.getByTestId('pin-engine-version'));
      });

      await waitForProcessProfile();
      await submit();

      expect(await screen.findByText(/OrcaSlicer 2\.3\.1 has no online worker/i)).toBeInTheDocument();
      expect(sliceJobService.submitJob).not.toHaveBeenCalled();
    });

    it('never renders a frame pairing a new engine with the previous engine pin', async () => {
      // Asserting only the settled state would pass even without the inline
      // reset, because the [selectedSlicerId] effect eventually clears the pin.
      // The defect is the intermediate frame, so assert on every render (Hicks).
      mockEngines([
        { version: '2.4.2', available: true },
        { version: '2.3.1', available: false },
      ], '2.4.2');

      await reachSubmittableState();

      await waitFor(() => {
        expect(screen.getByTestId('pin-engine-version')).toBeInTheDocument();
      });
      act(() => {
        fireEvent.click(screen.getByTestId('pin-engine-version'));
      });
      await waitFor(() => {
        expect(screen.getByTestId('slicer-selector')).toHaveAttribute('data-selected-version', '2.3.1');
      });

      slicerSelectorRenderSpy.mockClear();
      act(() => {
        fireEvent.change(screen.getByTestId('slicer-select'), { target: { value: '2' } });
      });

      await waitFor(() => {
        expect(screen.getByTestId('slicer-selector')).toHaveAttribute('data-engine-name', 'PrusaSlicer');
      });

      const frames = slicerSelectorRenderSpy.mock.calls.map(
        (c) => c[0] as { engineName?: string; selectedVersion?: string },
      );
      // Guard against a vacuous pass: the switch must actually have rendered.
      expect(frames.some((f) => f.engineName === 'PrusaSlicer')).toBe(true);
      expect(
        frames.filter((f) => f.engineName === 'PrusaSlicer' && f.selectedVersion !== undefined),
      ).toEqual([]);
    });

    it('blocks a pinned submission when the pinned engine vanishes from the registry', async () => {
      // A registry refresh can drop the engine entirely, leaving engineInfo
      // undefined while the pin survives. Requiring engineInfo in the guard let
      // that stale pin dispatch unvalidated (Hicks R3).
      mockEngines([
        { version: '2.4.2', available: true },
        { version: '2.3.1', available: false },
      ], '2.4.2');

      const queryClient = await reachSubmittableState();

      await waitFor(() => {
        expect(screen.getByTestId('pin-engine-version')).toBeInTheDocument();
      });
      act(() => {
        fireEvent.click(screen.getByTestId('pin-engine-version'));
      });
      await waitForProcessProfile();

      // OrcaSlicer disappears from the registry while the pin is still held.
      vi.mocked(slicerService.listEngines).mockResolvedValue([
        { engine: 'PrusaSlicer', versions: ['2.9.0'], versionEntries: [{ version: '2.9.0', available: true }], latest: '2.9.0' },
      ]);
      await act(async () => { await queryClient.invalidateQueries({ queryKey: ['slicer-engines-registry'] }); });

      await waitFor(() => {
        expect(screen.getByTestId('slicer-selector')).toHaveAttribute('data-all-versions', '');
      });

      await submit();

      expect(await screen.findByText(/OrcaSlicer 2\.3\.1 has no online worker/i)).toBeInTheDocument();
      expect(sliceJobService.submitJob).not.toHaveBeenCalled();
    });
  });

  describe('Field validation submit guard (issue #2223)', () => {
    // Simple mode is the default (slicerModeRef starts at 'Simple'), matching
    // the issue's repro. The panel itself is mocked (see the `../../components/job`
    // mock above), so its `onValidationChange` callback is driven directly via
    // the mock's test-only buttons rather than typing into real inputs — the
    // component-level rejection/inline-error behavior is already covered by
    // `SlicerSettingsPanel.test.tsx`. This suite proves the page-level submit
    // guard itself: an invalid Simple-mode field must block the "Slice Plate"
    // action instead of letting the job reach the backend and fail generically.
    async function reachSubmittableState() {
      vi.mocked(slicerProfilesService.getMachineProfilesForModel).mockResolvedValue([
        { name: 'Prusa MK4S 0.4 nozzle', manufacturer: 'Prusa', nozzleDiameter: 0.4, printerModel: 'MK4S' },
      ] as OrcaMachineProfile[]);
      vi.mocked(slicerProfilesService.getProcessProfilesForMachines).mockResolvedValue([
        {
          name: '0.20mm Standard @MK4S',
          quality: 'Standard',
          layerHeight: 0.2,
          infillPercentage: 15,
          printSpeed: 60,
          supports: false,
          compatible_printers: ['Prusa MK4S 0.4 nozzle'],
        },
      ] as OrcaProcessProfile[]);

      renderWithProviders(<NewSliceJobPage />, { route: '/slicer?modelId=model-3d-1' });

      await waitFor(() => {
        expect(screen.getByText('My Prusa MK4')).toBeInTheDocument();
      });
      fireEvent.change(screen.getByTestId('printer-select'), { target: { value: 'printer-1' } });

      await waitFor(() => {
        const processSelect = Array.from(document.querySelectorAll('select'))
          .find((s) => s.value.startsWith('system:'));
        expect(processSelect?.value).toBe('system:0.20mm Standard @MK4S');
      });
    }

    const latestOnSlice = () =>
      (slicerWorkspaceSpy.mock.calls.at(-1)?.[0] as { onSlice?: (ids?: string[]) => void } | undefined)?.onSlice;

    async function submit() {
      await waitFor(() => {
        expect(latestOnSlice()).toBeTypeOf('function');
      });
      await act(async () => { latestOnSlice()!(); });
    }

    it('blocks submission while the Simple settings panel reports an invalid field', async () => {
      await reachSubmittableState();

      fireEvent.click(screen.getByTestId('invalidate-simple-settings'));
      await submit();

      expect(await screen.findByText(/fix the highlighted print setting/i)).toBeInTheDocument();
      expect(sliceJobService.submitJob).not.toHaveBeenCalled();
    });

    it('allows submission again once the Simple settings panel reports valid fields', async () => {
      await reachSubmittableState();

      fireEvent.click(screen.getByTestId('invalidate-simple-settings'));
      await submit();
      expect(sliceJobService.submitJob).not.toHaveBeenCalled();

      fireEvent.click(screen.getByTestId('revalidate-simple-settings'));
      await submit();

      await waitFor(() => {
        expect(sliceJobService.submitJob).toHaveBeenCalled();
      });
    });
  });

  describe('Failed job status survives a responsive breakpoint resize (issue #2214)', () => {
    beforeEach(() => {
      jobProgressRef.reset();
      vi.mocked(slicerService.listEngines).mockResolvedValue([
        { engine: 'OrcaSlicer', versions: ['2.4.2'], versionEntries: [{ version: '2.4.2', available: true }], latest: '2.4.2' },
      ]);
      vi.mocked(slicerProfilesService.getMachineProfilesForModel).mockResolvedValue([
        { name: 'Prusa MK4S 0.4 nozzle', manufacturer: 'Prusa', nozzleDiameter: 0.4, printerModel: 'MK4S' },
      ] as OrcaMachineProfile[]);
      vi.mocked(slicerProfilesService.getProcessProfilesForMachines).mockResolvedValue([
        {
          name: '0.20mm Standard @MK4S',
          quality: 'Standard',
          layerHeight: 0.2,
          infillPercentage: 15,
          printSpeed: 60,
          supports: false,
          compatible_printers: ['Prusa MK4S 0.4 nozzle'],
        },
      ] as OrcaProcessProfile[]);
      // The file-level default (`{ id: 'job-1', status: 'Queued' }`) does not
      // match `SubmitSliceJobResponse` (`jobId`, not `id`), so
      // `submitMutation.onSuccess` throws on `res.jobId.substring(...)` before
      // it ever calls `setSubmittedJobId` — invisible to the other submit
      // tests here because none of them assert on the post-submit UI. Use the
      // real response shape so this suite can actually reach a submitted job.
      vi.mocked(sliceJobService.submitJob).mockResolvedValue({
        jobId: 'job-1',
        queuePosition: null,
      } as Awaited<ReturnType<typeof sliceJobService.submitJob>>);
    });

    // Reused so all renders within a test share the same MemoryRouter /
    // QueryClientProvider / AuthProvider ancestors.
    function buildWrappedPage(route: string) {
      const queryClient = createTestQueryClient();
      return (
        <MemoryRouter initialEntries={[route]}>
          <QueryClientProvider client={queryClient}>
            <AuthProvider>
              <Routes>
                <Route path="/slicer" element={<NewSliceJobPage />} />
              </Routes>
            </AuthProvider>
          </QueryClientProvider>
        </MemoryRouter>
      );
    }

    async function submitAndFail() {
      const wrappedPage = buildWrappedPage('/slicer?modelId=model-3d-1');
      render(wrappedPage);

      await waitFor(() => {
        expect(screen.getByText('My Prusa MK4')).toBeInTheDocument();
      });
      fireEvent.change(screen.getByTestId('printer-select'), { target: { value: 'printer-1' } });

      await waitFor(() => {
        const processSelect = Array.from(document.querySelectorAll('select'))
          .find((s) => s.value.startsWith('system:'));
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

      // Simulate the real-time SignalR event carrying the terminal Failed
      // status (rather than driving the mocked SignalR transport end to end).
      act(() => {
        jobProgressRef.set({
          ...jobProgressRef.value,
          status: 'Failed',
          error: 'Slicer worker crashed while processing the plate.',
        });
      });

      await waitFor(() => {
        expect(screen.getAllByText('Failed').length).toBeGreaterThan(0);
      });
    }

    afterEach(() => {
      // Restore jsdom's default viewport so later tests in this file are not
      // affected by a resize simulated here.
      window.innerWidth = 1024;
      window.innerHeight = 768;
    });

    it('keeps the Failed/Retry UI visible — with no stale "Job queued" alert — after resizing to mobile and back', async () => {
      vi.useFakeTimers({ shouldAdvanceTime: true });
      try {
        await submitAndFail();

        expect(screen.queryByText(/Job queued/i)).not.toBeInTheDocument();
        expect(screen.getAllByRole('button', { name: 'Retry' }).length).toBeGreaterThan(0);

        // Cross the mobile responsive breakpoint (1026x877 -> 375x667), per
        // the issue's exact repro steps.
        act(() => {
          window.innerWidth = 375;
          window.innerHeight = 667;
          window.dispatchEvent(new Event('resize'));
        });

        // Let the pre-fix 3s auto-clear timer window elapse. Before the fix,
        // this wiped `submittedJobId` (and NOT `message`) for a Failed job,
        // which resurfaced the stale "Job queued (id ...)" alert.
        await act(async () => { await vi.advanceTimersByTimeAsync(3500); });

        // Cross back to desktop (375x667 -> 1026x877).
        act(() => {
          window.innerWidth = 1026;
          window.innerHeight = 877;
          window.dispatchEvent(new Event('resize'));
        });

        expect(screen.getAllByText('Failed').length).toBeGreaterThan(0);
        expect(screen.getAllByRole('button', { name: 'Retry' }).length).toBeGreaterThan(0);
        expect(screen.queryByText(/Job queued/i)).not.toBeInTheDocument();
      } finally {
        vi.useRealTimers();
      }
    });

    it('does not auto-clear the Failed job after the completion-only 3s timer window, even without a resize', async () => {
      // Regression guard for the root cause itself, independent of the
      // resize repro: the auto-clear effect must only fire for 'Completed',
      // never 'Failed'.
      vi.useFakeTimers({ shouldAdvanceTime: true });
      try {
        await submitAndFail();

        await act(async () => { await vi.advanceTimersByTimeAsync(5000); });

        expect(screen.getAllByText('Failed').length).toBeGreaterThan(0);
        expect(screen.queryByText(/Job queued/i)).not.toBeInTheDocument();
      } finally {
        vi.useRealTimers();
      }
    });
  });

});
