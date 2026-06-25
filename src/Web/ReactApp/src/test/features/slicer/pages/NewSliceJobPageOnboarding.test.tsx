import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import { MemoryRouter, Route, Routes } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import React from 'react';
import { NewSliceJobPage } from '@/features/slicer/pages/NewSliceJobPage';
import { AuthProvider } from '@/common/contexts/AuthContext';

const mockNavigate = vi.fn();
vi.mock('react-router', async () => {
  const actual = await vi.importActual<typeof import('react-router')>('react-router');
  return {
    ...actual,
    useNavigate: () => mockNavigate,
  };
});

// Mock slicer profiles service
const mockListExtended = vi.fn();
vi.mock('@/services/slicerProfilesService', () => ({
  slicerProfilesService: {
    listExtended: (...args: unknown[]) => mockListExtended(...args),
    getMachineProfilesForModel: vi.fn(() => Promise.resolve([])),
    getFilamentProfilesForMachines: vi.fn(() => Promise.resolve([])),
    getProcessProfilesForMachines: vi.fn(() => Promise.resolve([])),
    listCustomProfiles: vi.fn(() => Promise.resolve({ profiles: [], totalCount: 0 })),
  },
  // Re-export types referenced by the page
  OrcaMachineProfile: {},
  OrcaFilamentProfile: {},
  OrcaProcessProfile: {},
}));

// Mock API client
vi.mock('@/services/api', () => ({
  apiClient: {
    get: vi.fn(() => Promise.resolve({ data: [] })),
    post: vi.fn(),
    put: vi.fn(),
    delete: vi.fn(),
    getPrinters: vi.fn(() => Promise.resolve([])),
    getPrinterDetails: vi.fn(() => Promise.resolve(null)),
  },
}));

// Mock slicer registry
vi.mock('@/services/slicerRegistry', () => ({
  slicerRegistry: {
    getSlicers: vi.fn(() => Promise.resolve([])),
  },
}));

// Mock slice job service
vi.mock('@/services/sliceJobService', () => ({
  sliceJobService: {
    submit: vi.fn(),
    parseOrcaNumeric: vi.fn(() => undefined),
  },
  SubmitSliceJobRequest: {},
}));

// Mock asset service
vi.mock('@/services/assetService', () => ({
  assetService: {
    getAsset: vi.fn(() => null),
    getCoverImageUrl: vi.fn(() => null),
    getFallbackImageUrl: vi.fn(() => '/assets/printers/generic-printer.svg'),
    getCoverImageUrlWithFallback: vi.fn(() => '/assets/printers/generic-printer.svg'),
  },
}));

// Mock useAuth hook
vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: vi.fn(() => ({
    user: { id: 'user-1', email: 'test@example.com' },
    isAuthenticated: true,
    isLoading: false,
  })),
}));

// Mock heavy sub-components to keep tests fast and focused
vi.mock('@/features/slicer/components/job', () => ({
  PrinterSlicerSelector: () => <div data-testid="printer-slicer-selector">PrinterSelector</div>,
  SlicerSelector: () => <div data-testid="slicer-selector">SlicerSelector</div>,
  SlicerSettingsPanel: () => <div data-testid="slicer-settings-panel">Settings</div>,
}));

vi.mock('@/features/models3d/components/3d/ModelViewer3D', () => ({
  ModelViewer: () => <div data-testid="model-viewer">ModelViewer</div>,
}));

vi.mock('@/features/models3d/components/3d/STLPreviewModal', () => ({
  STLPreviewModal: () => null,
}));

vi.mock('@/features/models3d/components/3d/ViewerSkeleton', () => ({
  ViewerSkeleton: () => <div>Loading...</div>,
}));

vi.mock('@/features/slicer/components/CloneProfilesModal', () => ({
  CloneProfilesModal: () => null,
}));

vi.mock('@/features/slicer/components/ProfileEditorModal', () => ({
  ProfileEditorModal: () => null,
}));

vi.mock('@/features/slicer/components/settings', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/features/slicer/components/settings')>();
  return {
    ...actual,
    SlicerSettingsPanel: () => <div data-testid="slicer-settings-panel">Settings</div>,
  };
});

vi.mock('@/common/hooks/useSTLFile', () => ({
  useSTLFile: vi.fn(() => ({ file: null, setFile: vi.fn(), clearFile: vi.fn() })),
}));

vi.mock('@/features/slicer/hooks/useSliceJobProgress', () => ({
  useSliceJobProgress: vi.fn(() => ({
    status: null,
    progressPercent: 0,
    message: null,
    error: null,
  })),
}));

vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getHubUrl: vi.fn(() => 'http://localhost:5245/hubs/slicer'),
  getApiBaseUrl: vi.fn(() => 'http://localhost:5245'),
}));

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: vi.fn(() => ({
    withUrl: vi.fn().mockReturnThis(),
    withAutomaticReconnect: vi.fn().mockReturnThis(),
    build: vi.fn(() => ({
      start: vi.fn(() => Promise.resolve()),
      stop: vi.fn(() => Promise.resolve()),
      on: vi.fn(),
      off: vi.fn(),
      state: 'Disconnected',
    })),
  })),
  HttpTransportType: { WebSockets: 1 },
  HubConnectionState: { Disconnected: 'Disconnected' },
}));

const createTestQueryClient = () =>
  new QueryClient({
    defaultOptions: {
      queries: { retry: false, staleTime: Infinity },
      mutations: { retry: false },
    },
  });

function renderPage(route = '/slicer') {
  const queryClient = createTestQueryClient();
  return {
    ...render(
      <MemoryRouter initialEntries={[route]}>
        <QueryClientProvider client={queryClient}>
          <AuthProvider>
            <Routes>
              <Route path="/slicer" element={<NewSliceJobPage />} />
            </Routes>
          </AuthProvider>
        </QueryClientProvider>
      </MemoryRouter>,
    ),
    queryClient,
  };
}

describe('NewSliceJobPage — Onboarding', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('shows onboarding banner when no machine profiles exist', async () => {
    mockListExtended.mockResolvedValue({
      machineProfiles: [],
      filamentProfiles: [],
      processProfiles: [],
    });

    renderPage();

    await waitFor(() => {
      expect(screen.getByTestId('onboarding-banner')).toBeInTheDocument();
    });

    expect(screen.getByText('Get started with slicing')).toBeInTheDocument();
    expect(screen.getByText(/Import printer profiles/)).toBeInTheDocument();
  });

  it('shows normal form when machine profiles exist', async () => {
    mockListExtended.mockResolvedValue({
      machineProfiles: [
        { id: 'mp-1', name: 'Prusa MK4 0.4', profileType: 'machine', manufacturer: 'Prusa' },
      ],
      filamentProfiles: [],
      processProfiles: [],
    });

    renderPage();

    await waitFor(() => {
      expect(screen.getByTestId('printer-slicer-selector')).toBeInTheDocument();
    });

    expect(screen.queryByTestId('onboarding-banner')).not.toBeInTheDocument();
  });

  it('"Import Profiles" button navigates to import wizard', async () => {
    mockListExtended.mockResolvedValue({
      machineProfiles: [],
      filamentProfiles: [],
      processProfiles: [],
    });

    const user = userEvent.setup();
    renderPage();

    await waitFor(() => {
      expect(screen.getByTestId('import-profiles-button')).toBeInTheDocument();
    });

    await user.click(screen.getByTestId('import-profiles-button'));
    expect(mockNavigate).toHaveBeenCalledWith('/profiles/import');
  });

  it('shows normal UI when only custom machine profiles exist (no system profiles)', async () => {
    mockListExtended.mockResolvedValue({
      machineProfiles: [
        { id: 'custom-1', name: 'My Custom Printer', profileType: 'machine', manufacturer: 'Custom', isSystem: false },
      ],
      filamentProfiles: [],
      processProfiles: [],
    });

    renderPage();

    await waitFor(() => {
      expect(screen.getByTestId('printer-slicer-selector')).toBeInTheDocument();
    });

    expect(screen.queryByTestId('onboarding-banner')).not.toBeInTheDocument();
  });

  it('"Create Custom Profile" button navigates to profile editor', async () => {
    mockListExtended.mockResolvedValue({
      machineProfiles: [],
      filamentProfiles: [],
      processProfiles: [],
    });

    const user = userEvent.setup();
    renderPage();

    await waitFor(() => {
      expect(screen.getByTestId('create-custom-profile-button')).toBeInTheDocument();
    });

    await user.click(screen.getByTestId('create-custom-profile-button'));
    expect(mockNavigate).toHaveBeenCalledWith('/profiles');
  });

  it('onboarding banner mentions both importing and creating profiles', async () => {
    mockListExtended.mockResolvedValue({
      machineProfiles: [],
      filamentProfiles: [],
      processProfiles: [],
    });

    renderPage();

    await waitFor(() => {
      expect(screen.getByTestId('onboarding-banner')).toBeInTheDocument();
    });

    expect(screen.getByText(/Import printer profiles or create custom ones/)).toBeInTheDocument();
    expect(screen.getByTestId('import-profiles-button')).toBeInTheDocument();
    expect(screen.getByTestId('create-custom-profile-button')).toBeInTheDocument();
  });
});
