import '@testing-library/jest-dom';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render } from '@testing-library/react';
import { screen, fireEvent } from '@testing-library/dom';
import { EnhancedPrinterCard } from '@/features/printers/components/EnhancedPrinterCard';
import { PrinterBackend, type Printer } from '@/types/api';
import { AuthProvider } from '@/common/contexts/AuthContext';

const basePrinter: Printer = {
  id: 'octo-2',
  name: 'OctoPrint Enhanced',
  serverUrl: 'http://octoprint.local',
  isOnline: true,
  isReachable: true,
  backend: PrinterBackend.OctoPrint,
  manufacturerName: 'Prusa',
  modelName: 'MK3S',
};


// Mock hasPermission to always return true
vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission: () => true })
}));

// Mock useSignalR to prevent signalRService errors
vi.mock('@/hooks/useSignalR', () => ({
  usePrinterStatusUpdates: () => ({ printerStatuses: new Map() }),
  useDiscoveryStream: () => ({
    progress: null,
    foundPrinters: [],
    completed: false,
    resetDiscovery: vi.fn(),
    isActive: false,
    isCompleted: false,
  }),
}));

describe('EnhancedPrinterCard (OctoPrint)', () => {
  afterEach(() => vi.clearAllMocks());

  function renderWithAuthProvider(ui: React.ReactElement) {
    return render(<AuthProvider>{ui}</AuthProvider>);
  }

  it('shows camera snapshot if present', () => {
    renderWithAuthProvider(
      <EnhancedPrinterCard printer={{ ...basePrinter, cameraSnapshotUrl: 'http://cam/snap.jpg' }} />
    );
    // Expand the card to show camera button
    fireEvent.click(screen.getByTitle(/expand/i));
    fireEvent.click(screen.getByTitle(/show camera/i));
    expect(screen.getByAltText(/camera snapshot/i)).toHaveAttribute('src', expect.stringContaining('snap.jpg'));
  });

  it('falls back to stream if no snapshot', () => {
    renderWithAuthProvider(
      <EnhancedPrinterCard printer={{ ...basePrinter, cameraStreamUrl: 'http://cam/stream.mjpg' }} />
    );
    // Expand the card to show camera button
    fireEvent.click(screen.getByTitle(/expand/i));
    fireEvent.click(screen.getByTitle(/show camera/i));
    expect(screen.getByAltText(/camera snapshot/i)).toHaveAttribute('src', expect.stringContaining('stream.mjpg'));
    expect(screen.getByText(/live stream only/i)).toBeInTheDocument();
  });

  it('shows Pause/Resume controls for OctoPrint', () => {
    renderWithAuthProvider(
      <EnhancedPrinterCard printer={{ ...basePrinter }} />
    );
    // Expand the card to show controls
    fireEvent.click(screen.getByTitle(/expand/i));
    expect(screen.getByText(/print controls/i)).toBeInTheDocument();
  });
});
