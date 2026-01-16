import React from 'react';
import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// Mock logging hook to avoid noisy logs
vi.mock('@/common/hooks/useUnifiedLogging', () => ({
  useUnifiedLogging: () => ({ logger: { info: vi.fn(), warn: vi.fn(), error: vi.fn() } }),
}));

// Mock services that perform network or SignalR work
vi.mock('@/services/assetService', () => {
  const mockAssetService = { 
    initialize: vi.fn().mockResolvedValue(undefined),
    getAssets: vi.fn().mockReturnValue({}),
    getManufacturer: vi.fn(),
    getPrinter: vi.fn(),
    getPrintersByName: vi.fn(),
    getPrintersByManufacturer: vi.fn(),
  };
  return { assetService: mockAssetService };
});
vi.mock('@/services/printer-signalr', () => ({ printerSignalRService: { connect: vi.fn().mockResolvedValue(undefined) } }));
vi.mock('@/services/harvest-signalr', () => ({ signalRService: { connect: vi.fn().mockResolvedValue(undefined) } }));

// Mock API helpers to keep network calls predictable
vi.mock('@/common/utils/apiUrlHelpers', () => ({ getApiBaseUrl: () => 'http://localhost:5245', getAuthHeaders: () => ({}) }));

// Mock providers and components used by App to keep rendering lightweight
vi.mock('@/contexts/ThemeContext', () => ({ ThemeProvider: ({ children }: { children: React.ReactNode }) => <>{children}</> }));
vi.mock('@/common/contexts/AuthContext', () => ({ AuthProvider: ({ children }: { children: React.ReactNode }) => <>{children}</> }));
vi.mock('@/contexts/SlicerUIContext', () => ({ SlicerUIProvider: ({ children }: { children: React.ReactNode }) => <>{children}</> }));
vi.mock('@/features/auth/components/SetupWizard', () => ({ SetupWizard: () => <div>SetupWizardMock</div> }));
vi.mock('@/common/components/ErrorBoundary', () => ({ ErrorBoundary: ({ children }: { children: React.ReactNode }) => <>{children}</> }));

// Minimal mock for Toaster so it doesn't render complex UI
vi.mock('sonner', () => ({ Toaster: ({ children }: { children: React.ReactNode }) => <>{children}</> }));

import App from '../App';

describe('App smoke', () => {
  beforeEach(() => {
    // Mock global fetch used by App to check setup status
    (global as unknown as { fetch?: typeof fetch }).fetch = vi.fn().mockResolvedValue({ ok: true, json: async () => ({ needsSetup: true }) });
  });

  afterEach(() => {
    vi.resetAllMocks();
    // delete the mocked fetch from global safely
    delete (global as unknown as { fetch?: typeof fetch }).fetch;
  });

  it('renders the setup wizard when setup is required', async () => {
    render(<App />);
    expect(await screen.findByText('SetupWizardMock')).toBeTruthy();
  });
});
