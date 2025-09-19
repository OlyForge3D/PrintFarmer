import '@testing-library/jest-dom';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { PrinterCard } from '@/components/PrinterCard';
import { PrinterBackend, type Printer } from '@/types/api';
import { AuthProvider } from '@/contexts/AuthContext';

const basePrinter: Printer = {
  id: 'octo-1',
  name: 'OctoPrint Test',
  serverUrl: 'http://octoprint.local',
  isOnline: true,
  isReachable: true,
  backend: PrinterBackend.OctoPrint,
  manufacturerName: 'Prusa',
  modelName: 'MK3S',
};


// Mock hasPermission to always return true
vi.mock('@/contexts/AuthHooks', () => ({
  useAuth: () => ({ hasPermission: () => true })
}));

// Mock useSignalR to prevent signalRService errors
vi.mock('@/hooks/useSignalR', () => ({
  usePrinterStatusUpdates: () => ({ getPrinterStatus: () => undefined }),
  useDiscoveryStream: () => ({
    progress: null,
    foundPrinters: [],
    completed: false,
    resetDiscovery: vi.fn(),
    isActive: false,
    isCompleted: false,
  }),
}));

describe('PrinterCard (OctoPrint)', () => {
  afterEach(() => vi.clearAllMocks());

  function renderWithAuthProvider(ui: React.ReactElement) {
    return render(<AuthProvider>{ui}</AuthProvider>);
  }

  it('shows camera snapshot if present', () => {
    renderWithAuthProvider(
      <PrinterCard printer={{ ...basePrinter, cameraSnapshotUrl: 'http://cam/snap.jpg' }} />
    );
    expect(screen.getByAltText(/camera/i)).toHaveAttribute('src', expect.stringContaining('snap.jpg'));
  });

  it('falls back to stream if no snapshot', () => {
    renderWithAuthProvider(
      <PrinterCard printer={{ ...basePrinter, cameraStreamUrl: 'http://cam/stream.mjpg' }} />
    );
    expect(screen.getByAltText(/camera/i)).toHaveAttribute('src', expect.stringContaining('stream.mjpg'));
    expect(screen.getByText(/live stream only/i)).toBeInTheDocument();
  });

  it('shows Pause/Resume controls for OctoPrint', () => {
    renderWithAuthProvider(
      <PrinterCard printer={{ ...basePrinter }} />
    );
    expect(screen.getByText(/manage/i)).toBeInTheDocument();
  });
});
