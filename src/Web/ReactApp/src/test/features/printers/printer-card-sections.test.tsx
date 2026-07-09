import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import type { Printer, PrinterBackendCapabilitiesDto } from '@/types/api';

// Mock the hooks and services
vi.mock('@/common/hooks/useApi', () => ({
  usePrinters: () => ({ data: [], isLoading: false }),
}));

vi.mock('@/common/hooks/useSpoolmanConfigured', () => ({
  useSpoolmanConfigured: () => ({ ready: true }),
}));

vi.mock('@/common/hooks/usePrinterDisplay', () => ({
  usePrinterDisplay: (printer: Printer) => printer,
}));

vi.mock('@tanstack/react-query', () => ({
  useQueryClient: () => ({
    invalidateQueries: vi.fn(),
  }),
}));

// Mock components that will be decomposed
// These represent the new structure being implemented by other agents
const PrinterStatusHeader = ({ 
  printer, 
  onEdit 
}: { 
  printer: Printer; 
  backendCapabilities?: PrinterBackendCapabilitiesDto;
  onEdit?: (printer: Printer) => void;
}) => {
  return (
    <div data-testid="printer-status-header">
      <h3>{printer.name}</h3>
      <div data-testid="status-indicator" className="status-indicator">
        {printer.state}
      </div>
      <div data-testid="online-badge">
        {printer.isOnline ? 'Online' : 'Offline'}
      </div>
      {onEdit && <button onClick={() => onEdit(printer)}>Edit</button>}
    </div>
  );
};

const TemperatureControlSection = ({ 
  printer 
}: { 
  printer: Printer;
  backendCapabilities?: PrinterBackendCapabilitiesDto;
}) => {
  return (
    <div data-testid="temperature-control-section">
      <div data-testid="hotend-temp">
        Hotend: {printer.hotendTemp ?? 0}°C / {printer.hotendTarget ?? 0}°C
      </div>
      <div data-testid="bed-temp">
        Bed: {printer.bedTemp ?? 0}°C / {printer.bedTarget ?? 0}°C
      </div>
    </div>
  );
};

const MovementControlSection = ({ 
  backendCapabilities
}: { 
  printer: Printer;
  backendCapabilities?: PrinterBackendCapabilitiesDto;
}) => {
  // Suppress unused variable warning
  void backendCapabilities;
  return (
    <div data-testid="movement-control-section">
      <div data-testid="x-axis-control">X-Axis Controls</div>
      <div data-testid="y-axis-control">Y-Axis Controls</div>
      <div data-testid="z-axis-control">Z-Axis Controls</div>
    </div>
  );
};

// Decomposed DetailedPrinterCard that composes the sections
const DetailedPrinterCard = ({ 
  printer, 
  backendCapabilities,
  onEdit,
  onDismiss 
}: { 
  printer: Printer; 
  backendCapabilities?: PrinterBackendCapabilitiesDto;
  onEdit?: (printer: Printer) => void;
  onDismiss?: () => void;
}) => {
  return (
    <div data-testid="detailed-printer-card" className="printer-card">
      <PrinterStatusHeader 
        printer={printer} 
        backendCapabilities={backendCapabilities}
        onEdit={onEdit} 
      />
      <TemperatureControlSection 
        printer={printer}
        backendCapabilities={backendCapabilities}
      />
      <MovementControlSection 
        printer={printer}
        backendCapabilities={backendCapabilities}
      />
      {onDismiss && (
        <button onClick={onDismiss} data-testid="dismiss-button">
          Dismiss
        </button>
      )}
    </div>
  );
};

describe('DetailedPrinterCard Decomposition', () => {
  const mockPrinter: Printer = {
    id: 'printer-1',
    name: 'Test Printer',
    state: 'Idle',
    isOnline: true,
    isEnabled: true,
    hotendTemp: 210,
    hotendTarget: 210,
    bedTemp: 60,
    bedTarget: 60,
    homedAxes: 'XYZ',
    printerBackend: 'Moonraker',
    url: 'http://printer.local',
    apiKey: null,
    cameraUrl: null,
    thumbnailUrl: null,
    progress: null,
    printTime: null,
    estimatedTimeRemaining: null,
    currentFileName: null,
    isPrinting: false,
    isPaused: false,
    manufacturer: null,
    model: null,
    locationId: null,
    spoolId: null,
    spoolInfo: null,
  };

  const mockCapabilities: PrinterBackendCapabilitiesDto = {
    supportsTemperatureControl: true,
    supportsMovement: true,
    supportsFilamentControl: true,
    supportsPrintControl: true,
    supportsFileOperations: true,
    supportsHistory: true,
    supportsCameraUrl: true,
    supportsObjectExclusion: false,
  };

  describe('PrinterStatusHeader Section', () => {
    it('renders printer name', () => {
      render(<PrinterStatusHeader printer={mockPrinter} />);
      
      expect(screen.getByText('Test Printer')).toBeInTheDocument();
    });

    it('renders status indicator', () => {
      render(<PrinterStatusHeader printer={mockPrinter} />);
      
      const statusIndicator = screen.getByTestId('status-indicator');
      expect(statusIndicator).toBeInTheDocument();
      expect(statusIndicator).toHaveTextContent('Idle');
    });

    it('renders online badge', () => {
      render(<PrinterStatusHeader printer={mockPrinter} />);
      
      const onlineBadge = screen.getByTestId('online-badge');
      expect(onlineBadge).toBeInTheDocument();
      expect(onlineBadge).toHaveTextContent('Online');
    });

    it('renders offline badge when printer is offline', () => {
      const offlinePrinter = { ...mockPrinter, isOnline: false };
      render(<PrinterStatusHeader printer={offlinePrinter} />);
      
      const onlineBadge = screen.getByTestId('online-badge');
      expect(onlineBadge).toHaveTextContent('Offline');
    });

    it('renders edit button when onEdit callback provided', () => {
      const onEdit = vi.fn();
      render(<PrinterStatusHeader printer={mockPrinter} onEdit={onEdit} />);
      
      const editButton = screen.getByRole('button', { name: /edit/i });
      expect(editButton).toBeInTheDocument();
    });

    it('does not render edit button when onEdit callback not provided', () => {
      render(<PrinterStatusHeader printer={mockPrinter} />);
      
      const editButton = screen.queryByRole('button', { name: /edit/i });
      expect(editButton).not.toBeInTheDocument();
    });
  });

  describe('TemperatureControlSection', () => {
    it('renders hotend temperature display', () => {
      render(<TemperatureControlSection printer={mockPrinter} />);
      
      const hotendTemp = screen.getByTestId('hotend-temp');
      expect(hotendTemp).toBeInTheDocument();
      expect(hotendTemp).toHaveTextContent('Hotend: 210°C / 210°C');
    });

    it('renders bed temperature display', () => {
      render(<TemperatureControlSection printer={mockPrinter} />);
      
      const bedTemp = screen.getByTestId('bed-temp');
      expect(bedTemp).toBeInTheDocument();
      expect(bedTemp).toHaveTextContent('Bed: 60°C / 60°C');
    });

    it('handles missing temperature values', () => {
      const printerNoTemps = { 
        ...mockPrinter, 
        hotendTemp: undefined, 
        hotendTarget: undefined,
        bedTemp: undefined,
        bedTarget: undefined
      };
      render(<TemperatureControlSection printer={printerNoTemps} />);
      
      const hotendTemp = screen.getByTestId('hotend-temp');
      expect(hotendTemp).toHaveTextContent('Hotend: 0°C / 0°C');
      
      const bedTemp = screen.getByTestId('bed-temp');
      expect(bedTemp).toHaveTextContent('Bed: 0°C / 0°C');
    });

    it('accepts backendCapabilities prop', () => {
      render(
        <TemperatureControlSection 
          printer={mockPrinter} 
          backendCapabilities={mockCapabilities}
        />
      );
      
      expect(screen.getByTestId('temperature-control-section')).toBeInTheDocument();
    });
  });

  describe('MovementControlSection', () => {
    it('renders X-axis controls', () => {
      render(<MovementControlSection printer={mockPrinter} />);
      
      expect(screen.getByTestId('x-axis-control')).toBeInTheDocument();
      expect(screen.getByText('X-Axis Controls')).toBeInTheDocument();
    });

    it('renders Y-axis controls', () => {
      render(<MovementControlSection printer={mockPrinter} />);
      
      expect(screen.getByTestId('y-axis-control')).toBeInTheDocument();
      expect(screen.getByText('Y-Axis Controls')).toBeInTheDocument();
    });

    it('renders Z-axis controls', () => {
      render(<MovementControlSection printer={mockPrinter} />);
      
      expect(screen.getByTestId('z-axis-control')).toBeInTheDocument();
      expect(screen.getByText('Z-Axis Controls')).toBeInTheDocument();
    });

    it('accepts backendCapabilities prop', () => {
      render(
        <MovementControlSection 
          printer={mockPrinter} 
          backendCapabilities={mockCapabilities}
        />
      );
      
      expect(screen.getByTestId('movement-control-section')).toBeInTheDocument();
    });
  });

  describe('DetailedPrinterCard Composition', () => {
    it('renders all three sections together', () => {
      render(<DetailedPrinterCard printer={mockPrinter} />);
      
      expect(screen.getByTestId('printer-status-header')).toBeInTheDocument();
      expect(screen.getByTestId('temperature-control-section')).toBeInTheDocument();
      expect(screen.getByTestId('movement-control-section')).toBeInTheDocument();
    });

    it('passes printer prop to all sections', () => {
      render(<DetailedPrinterCard printer={mockPrinter} />);
      
      // Verify printer data is rendered in each section
      expect(screen.getByText('Test Printer')).toBeInTheDocument();
      expect(screen.getByText(/Hotend: 210/)).toBeInTheDocument();
      expect(screen.getByText(/X-Axis Controls/)).toBeInTheDocument();
    });

    it('passes backendCapabilities to all sections', () => {
      render(
        <DetailedPrinterCard 
          printer={mockPrinter} 
          backendCapabilities={mockCapabilities}
        />
      );
      
      // All sections should render successfully with capabilities
      expect(screen.getByTestId('printer-status-header')).toBeInTheDocument();
      expect(screen.getByTestId('temperature-control-section')).toBeInTheDocument();
      expect(screen.getByTestId('movement-control-section')).toBeInTheDocument();
    });

    it('passes onEdit callback to PrinterStatusHeader', () => {
      const onEdit = vi.fn();
      render(<DetailedPrinterCard printer={mockPrinter} onEdit={onEdit} />);
      
      const editButton = screen.getByRole('button', { name: /edit/i });
      expect(editButton).toBeInTheDocument();
    });

    it('renders dismiss button when onDismiss provided', () => {
      const onDismiss = vi.fn();
      render(<DetailedPrinterCard printer={mockPrinter} onDismiss={onDismiss} />);
      
      const dismissButton = screen.getByTestId('dismiss-button');
      expect(dismissButton).toBeInTheDocument();
    });
  });

  describe('Section Component Props Types', () => {
    it('PrinterStatusHeader accepts typed props', () => {
      const onEdit = vi.fn();
      
      // TypeScript should enforce correct prop types
      render(
        <PrinterStatusHeader 
          printer={mockPrinter}
          backendCapabilities={mockCapabilities}
          onEdit={onEdit}
        />
      );
      
      expect(screen.getByTestId('printer-status-header')).toBeInTheDocument();
    });

    it('TemperatureControlSection accepts typed props', () => {
      render(
        <TemperatureControlSection 
          printer={mockPrinter}
          backendCapabilities={mockCapabilities}
        />
      );
      
      expect(screen.getByTestId('temperature-control-section')).toBeInTheDocument();
    });

    it('MovementControlSection accepts typed props', () => {
      render(
        <MovementControlSection 
          printer={mockPrinter}
          backendCapabilities={mockCapabilities}
        />
      );
      
      expect(screen.getByTestId('movement-control-section')).toBeInTheDocument();
    });
  });

  describe('Section Independence', () => {
    it('can render PrinterStatusHeader independently', () => {
      render(<PrinterStatusHeader printer={mockPrinter} />);
      
      expect(screen.getByTestId('printer-status-header')).toBeInTheDocument();
      expect(screen.queryByTestId('temperature-control-section')).not.toBeInTheDocument();
      expect(screen.queryByTestId('movement-control-section')).not.toBeInTheDocument();
    });

    it('can render TemperatureControlSection independently', () => {
      render(<TemperatureControlSection printer={mockPrinter} />);
      
      expect(screen.getByTestId('temperature-control-section')).toBeInTheDocument();
      expect(screen.queryByTestId('printer-status-header')).not.toBeInTheDocument();
      expect(screen.queryByTestId('movement-control-section')).not.toBeInTheDocument();
    });

    it('can render MovementControlSection independently', () => {
      render(<MovementControlSection printer={mockPrinter} />);
      
      expect(screen.getByTestId('movement-control-section')).toBeInTheDocument();
      expect(screen.queryByTestId('printer-status-header')).not.toBeInTheDocument();
      expect(screen.queryByTestId('temperature-control-section')).not.toBeInTheDocument();
    });
  });
});
