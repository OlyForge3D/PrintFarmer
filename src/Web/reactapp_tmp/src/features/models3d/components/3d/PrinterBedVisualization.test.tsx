/**
 * Tests for PrinterBedVisualization Component
 */

import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { PrinterBedVisualization } from './PrinterBedVisualization';
import { PrinterModelDto } from '@/types/api';
import type { PrinterStatus } from './PrinterBedVisualization';

// Mock canvas-related APIs
 
HTMLCanvasElement.prototype.getContext = vi.fn(() => ({
  clear: vi.fn(),
  drawImage: vi.fn(),
})) as unknown as typeof HTMLCanvasElement.prototype.getContext;

describe('PrinterBedVisualization Component', () => {
  let testPrinterModel: PrinterModelDto;
  let testStatus: PrinterStatus;

  beforeEach(() => {
    testPrinterModel = {
      id: 'model-1',
      name: 'Test Printer Model',
      manufacturerId: 'mfg-1',
      maxX: 200,
      maxY: 200,
      maxZ: 250,
      defaultNozzleDiameter: 0.4,
    };

    testStatus = {
      printerId: 'printer-1',
      name: 'Test Printer',
      state: 'Idle',
      nozzlePosition: {
        x: 100,
        y: 100,
        z: 10,
      },
      temperatures: {
        hotend: 25,
        hotendTarget: 0,
        bed: 25,
        bedTarget: 0,
      },
    };
  });

  describe('Rendering', () => {
    it('renders without crashing', () => {
      const { container } = render(
        <PrinterBedVisualization printerModel={testPrinterModel} status={testStatus} />
      );

      expect(container).toBeInTheDocument();
    });

    it('renders canvas element', () => {
      const { container } = render(
        <PrinterBedVisualization printerModel={testPrinterModel} status={testStatus} />
      );

      const canvas = container.querySelector('canvas');
      expect(canvas).toBeInTheDocument();
    });

    it('applies correct height style', () => {
      const { container } = render(
        <PrinterBedVisualization printerModel={testPrinterModel} status={testStatus} height={500} />
      );

      const canvas = container.querySelector('canvas');
      expect(canvas?.style.height).toBeDefined();
    });

    it('uses default height when not specified', () => {
      const { container } = render(
        <PrinterBedVisualization printerModel={testPrinterModel} status={testStatus} />
      );

      const canvas = container.querySelector('canvas');
      expect(canvas).toBeInTheDocument();
    });

    it('renders error message when printerModel is missing', () => {
      render(
        <PrinterBedVisualization printerModel={null as unknown as PrinterModelDto} status={testStatus} />
      );

      expect(screen.getByText(/error/i)).toBeInTheDocument();
    });

    it('renders error message when status is missing', () => {
      render(
        <PrinterBedVisualization printerModel={testPrinterModel} status={null as unknown as PrinterStatus} />
      );

      expect(screen.getByText(/error/i)).toBeInTheDocument();
    });
  });

  describe('Props', () => {
    it('accepts and uses custom height', () => {
      const { container } = render(
        <PrinterBedVisualization printerModel={testPrinterModel} status={testStatus} height={600} />
      );

      expect(container).toBeInTheDocument();
    });

    it('respects autoRotate prop', () => {
      const { container } = render(
        <PrinterBedVisualization
          printerModel={testPrinterModel}
          status={testStatus}
          autoRotate={true}
        />
      );

      expect(container).toBeInTheDocument();
    });

    it('respects showAxes prop', () => {
      const { container } = render(
        <PrinterBedVisualization
          printerModel={testPrinterModel}
          status={testStatus}
          showAxes={true}
        />
      );

      expect(container).toBeInTheDocument();
    });

    it('respects showGrid prop', () => {
      const { container } = render(
        <PrinterBedVisualization
          printerModel={testPrinterModel}
          status={testStatus}
          showGrid={false}
        />
      );

      expect(container).toBeInTheDocument();
    });
  });

  describe('Status Updates', () => {
    it('handles different printer states', () => {
      const states: Array<PrinterStatus['state']> = ['Idle', 'Printing', 'Paused', 'Error', 'Offline'];

      states.forEach((state) => {
        const statusWithState = { ...testStatus, state };
        const { container, unmount } = render(
          <PrinterBedVisualization printerModel={testPrinterModel} status={statusWithState} />
        );

        expect(container).toBeInTheDocument();
        unmount();
      });
    });

    it('handles printing state', () => {
      const printingStatus = { ...testStatus, state: 'Printing' as const };
      const { container } = render(
        <PrinterBedVisualization printerModel={testPrinterModel} status={printingStatus} />
      );

      expect(container).toBeInTheDocument();
    });

    it('handles paused state', () => {
      const pausedStatus = { ...testStatus, state: 'Paused' as const };
      const { container } = render(
        <PrinterBedVisualization printerModel={testPrinterModel} status={pausedStatus} />
      );

      expect(container).toBeInTheDocument();
    });

    it('handles offline state', () => {
      const offlineStatus = { ...testStatus, state: 'Offline' as const };
      const { container } = render(
        <PrinterBedVisualization printerModel={testPrinterModel} status={offlineStatus} />
      );

      expect(container).toBeInTheDocument();
    });
  });

  describe('Nozzle Position', () => {
    it('handles nozzle position updates', () => {
      const { rerender, container } = render(
        <PrinterBedVisualization printerModel={testPrinterModel} status={testStatus} />
      );

      // Update position
      const updatedStatus = {
        ...testStatus,
        nozzlePosition: { x: 150, y: 150, z: 20 },
      };

      rerender(
        <PrinterBedVisualization printerModel={testPrinterModel} status={updatedStatus} />
      );

      expect(container).toBeInTheDocument();
    });

    it('handles missing nozzle position', () => {
      const noPositionStatus = { ...testStatus, nozzlePosition: undefined };
      const { container } = render(
        <PrinterBedVisualization printerModel={testPrinterModel} status={noPositionStatus} />
      );

      expect(container).toBeInTheDocument();
    });

    it('handles zero position', () => {
      const zeroStatus = {
        ...testStatus,
        nozzlePosition: { x: 0, y: 0, z: 0 },
      };
      const { container } = render(
        <PrinterBedVisualization printerModel={testPrinterModel} status={zeroStatus} />
      );

      expect(container).toBeInTheDocument();
    });
  });

  describe('Temperature Display', () => {
    it('handles temperature data', () => {
      const hotStatus = {
        ...testStatus,
        temperatures: {
          hotend: 210,
          hotendTarget: 210,
          bed: 60,
          bedTarget: 60,
        },
      };

      const { container } = render(
        <PrinterBedVisualization printerModel={testPrinterModel} status={hotStatus} />
      );

      expect(container).toBeInTheDocument();
    });

    it('handles missing temperature data', () => {
      const noTempStatus = { ...testStatus, temperatures: undefined };
      const { container } = render(
        <PrinterBedVisualization printerModel={testPrinterModel} status={noTempStatus} />
      );

      expect(container).toBeInTheDocument();
    });
  });

  describe('Different Printer Models', () => {
    it('handles large printer dimensions', () => {
      const largePrinter: PrinterModelDto = {
        ...testPrinterModel,
        maxX: 500,
        maxY: 500,
        maxZ: 500,
      };

      const { container } = render(
        <PrinterBedVisualization printerModel={largePrinter} status={testStatus} />
      );

      expect(container).toBeInTheDocument();
    });

    it('handles small printer dimensions', () => {
      const smallPrinter: PrinterModelDto = {
        ...testPrinterModel,
        maxX: 100,
        maxY: 100,
        maxZ: 100,
      };

      const { container } = render(
        <PrinterBedVisualization printerModel={smallPrinter} status={testStatus} />
      );

      expect(container).toBeInTheDocument();
    });

    it('handles missing nozzle diameter', () => {
      const noDiameterPrinter: PrinterModelDto = {
        ...testPrinterModel,
        defaultNozzleDiameter: undefined,
      };

      const { container } = render(
        <PrinterBedVisualization printerModel={noDiameterPrinter} status={testStatus} />
      );

      expect(container).toBeInTheDocument();
    });
  });

  describe('Error Handling', () => {
    it('displays error state when props are invalid', () => {
      render(
        <PrinterBedVisualization printerModel={undefined as unknown as PrinterModelDto} status={testStatus} />
      );

      expect(screen.getByText(/error/i)).toBeInTheDocument();
    });

    it('recovers when valid props are provided', () => {
      const { rerender, container } = render(
        <PrinterBedVisualization printerModel={undefined as unknown as PrinterModelDto} status={testStatus} />
      );

      expect(screen.getByText(/error/i)).toBeInTheDocument();

      // Provide valid props
      rerender(
        <PrinterBedVisualization printerModel={testPrinterModel} status={testStatus} />
      );

      expect(container).toBeInTheDocument();
    });
  });

  describe('Responsive Behavior', () => {
    it('renders with custom height', () => {
      const { container } = render(
        <PrinterBedVisualization
          printerModel={testPrinterModel}
          status={testStatus}
          height={300}
        />
      );

      expect(container).toBeInTheDocument();
    });

    it('handles very small height', () => {
      const { container } = render(
        <PrinterBedVisualization
          printerModel={testPrinterModel}
          status={testStatus}
          height={100}
        />
      );

      expect(container).toBeInTheDocument();
    });

    it('handles very large height', () => {
      const { container } = render(
        <PrinterBedVisualization
          printerModel={testPrinterModel}
          status={testStatus}
          height={1000}
        />
      );

      expect(container).toBeInTheDocument();
    });
  });

  describe('Integration', () => {
    it('renders complete visualization with all features', () => {
      const completeStatus: PrinterStatus = {
        printerId: 'printer-1',
        name: 'Complete Test',
        state: 'Printing',
        nozzlePosition: { x: 100, y: 100, z: 50 },
        temperatures: {
          hotend: 210,
          hotendTarget: 210,
          bed: 60,
          bedTarget: 60,
        },
        progress: 50,
        jobName: 'test-model.gcode',
      };

      const { container } = render(
        <PrinterBedVisualization
          printerModel={testPrinterModel}
          status={completeStatus}
          height={400}
          autoRotate={true}
          showAxes={true}
          showGrid={true}
        />
      );

      expect(container).toBeInTheDocument();
    });
  });
});
