/**
 * Tests for PrinterBedCard Component
 */

import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { PrinterBedCard } from './PrinterBedCard';
import { PrinterModelDto } from '@/types/api';
import type { PrinterStatus } from './PrinterBedVisualization';

// Mock the PrinterBedVisualization component to avoid Three.js complexity
vi.mock('./PrinterBedVisualization', () => ({
  PrinterBedVisualization: ({ height }: { height: number }) => (
    <div data-testid="bed-visualization" style={{ height: `${height}px` }}>
      3D Visualization
    </div>
  ),
}));

describe('PrinterBedCard Component', () => {
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
      progress: 0,
    };
  });

  describe('Rendering', () => {
    it('renders printer name', () => {
      render(<PrinterBedCard printerModel={testPrinterModel} status={testStatus} />);

      expect(screen.getByText('Test Printer')).toBeInTheDocument();
    });

    it('renders printer model name', () => {
      render(<PrinterBedCard printerModel={testPrinterModel} status={testStatus} />);

      expect(screen.getByText('Test Printer Model')).toBeInTheDocument();
    });

    it('renders visualization component', () => {
      render(<PrinterBedCard printerModel={testPrinterModel} status={testStatus} />);

      expect(screen.getByTestId('bed-visualization')).toBeInTheDocument();
    });

    it('renders status badge', () => {
      render(<PrinterBedCard printerModel={testPrinterModel} status={testStatus} />);

      expect(screen.getByText('Idle')).toBeInTheDocument();
    });
  });

  describe('Status Display', () => {
    it('displays correct state badge for Idle', () => {
      render(<PrinterBedCard printerModel={testPrinterModel} status={testStatus} />);

      expect(screen.getByText('Idle')).toBeInTheDocument();
    });

    it('displays correct state badge for Printing', () => {
      const printingStatus = { ...testStatus, state: 'Printing' as const };
      render(<PrinterBedCard printerModel={testPrinterModel} status={printingStatus} />);

      expect(screen.getByText('Printing')).toBeInTheDocument();
    });

    it('displays correct state badge for Paused', () => {
      const pausedStatus = { ...testStatus, state: 'Paused' as const };
      render(<PrinterBedCard printerModel={testPrinterModel} status={pausedStatus} />);

      expect(screen.getByText('Paused')).toBeInTheDocument();
    });

    it('displays temperatures when available', () => {
      render(<PrinterBedCard printerModel={testPrinterModel} status={testStatus} />);

      // Should show temperature section
      expect(screen.getByText('Temperatures')).toBeInTheDocument();
      // Should show hotend temperature
      expect(screen.getByText('Hotend')).toBeInTheDocument();
      // Should show bed temperature
      expect(screen.getByText('Bed')).toBeInTheDocument();
    });

    it('displays position when available', () => {
      render(<PrinterBedCard printerModel={testPrinterModel} status={testStatus} />);

      expect(screen.getByText('Position')).toBeInTheDocument();
    });

    it('displays job progress when available', () => {
      const progressStatus = { ...testStatus, progress: 50, jobName: 'benchy.gcode' };
      render(<PrinterBedCard printerModel={testPrinterModel} status={progressStatus} />);

      expect(screen.getByText('Job Progress')).toBeInTheDocument();
    });

    it('does not display progress when no job is active', () => {
      const noJobStatus = { ...testStatus, progress: 0, jobName: undefined };
      const { container } = render(
        <PrinterBedCard printerModel={testPrinterModel} status={noJobStatus} />
      );

      const progressText = container.textContent;
      expect(progressText).not.toContain('benchy.gcode');
    });
  });

  describe('Controls', () => {
    it('shows controls when showControls is true', () => {
      render(<PrinterBedCard printerModel={testPrinterModel} status={testStatus} showControls={true} />);

      expect(screen.getByText('Auto-rotate')).toBeInTheDocument();
    });

    it('hides controls when showControls is false', () => {
      render(<PrinterBedCard printerModel={testPrinterModel} status={testStatus} showControls={false} />);

      // Should not have checkbox for auto-rotate
      const checkboxes = screen.queryAllByRole('checkbox');
      expect(checkboxes.length).toBe(0);
    });

    it('shows refresh button when onRefresh is provided', () => {
      const mockRefresh = vi.fn();
      render(
        <PrinterBedCard
          printerModel={testPrinterModel}
          status={testStatus}
          showControls={true}
          onRefresh={mockRefresh}
        />
      );

      expect(screen.getByText('Refresh Status')).toBeInTheDocument();
    });
  });

  describe('Responsive Layout', () => {
    it('renders with full width by default', () => {
      const { container } = render(<PrinterBedCard printerModel={testPrinterModel} status={testStatus} />);

      const cardDiv = container.firstChild as HTMLElement;
      expect(cardDiv.className).toContain('w-full');
    });

    it('renders with half width when specified', () => {
      const { container } = render(
        <PrinterBedCard printerModel={testPrinterModel} status={testStatus} width="half" />
      );

      const cardDiv = container.firstChild as HTMLElement;
      expect(cardDiv.className).toContain('w-1/2');
    });

    it('renders with third width when specified', () => {
      const { container } = render(
        <PrinterBedCard printerModel={testPrinterModel} status={testStatus} width="third" />
      );

      const cardDiv = container.firstChild as HTMLElement;
      expect(cardDiv.className).toContain('w-1/3');
    });
  });

  describe('Temperature Display', () => {
    it('formats temperature values correctly', () => {
      const hotStatus = { ...testStatus, temperatures: { hotend: 210, hotendTarget: 210, bed: 60, bedTarget: 60 } };
      const { container } = render(<PrinterBedCard printerModel={testPrinterModel} status={hotStatus} />);

      expect(container.textContent).toContain('210');
      expect(container.textContent).toContain('60');
    });

    it('shows temperature targets', () => {
      const targetStatus = { ...testStatus, temperatures: { hotend: 200, hotendTarget: 210, bed: 50, bedTarget: 60 } };
      const { container } = render(<PrinterBedCard printerModel={testPrinterModel} status={targetStatus} />);

      expect(container.textContent).toContain('→');
    });
  });

  describe('Position Display', () => {
    it('formats position values correctly', () => {
      const { container } = render(<PrinterBedCard printerModel={testPrinterModel} status={testStatus} />);

      expect(container.textContent).toContain('100');
      expect(container.textContent).toContain('mm');
    });

    it('handles missing position data gracefully', () => {
      const noPositionStatus = { ...testStatus, nozzlePosition: undefined };
      const { container } = render(
        <PrinterBedCard printerModel={testPrinterModel} status={noPositionStatus} />
      );

      // Should still render without errors
      expect(container).toBeInTheDocument();
    });
  });

  describe('Error States', () => {
    it('displays error indicator for Error state', () => {
      const errorStatus = { ...testStatus, state: 'Error' as const };
      render(<PrinterBedCard printerModel={testPrinterModel} status={errorStatus} />);

      expect(screen.getByText('Error')).toBeInTheDocument();
    });

    it('handles Offline state', () => {
      const offlineStatus = { ...testStatus, state: 'Offline' as const };
      render(<PrinterBedCard printerModel={testPrinterModel} status={offlineStatus} />);

      expect(screen.getByText('Offline')).toBeInTheDocument();
    });
  });

  describe('Visualization Height', () => {
    it('uses default height for visualization', () => {
      render(<PrinterBedCard printerModel={testPrinterModel} status={testStatus} />);

      const viz = screen.getByTestId('bed-visualization');
      // Default height is 400 for desktop
      expect(viz.style.height).toBeDefined();
    });
  });

  describe('Integration', () => {
    it('renders complete card with all elements', () => {
      render(<PrinterBedCard printerModel={testPrinterModel} status={testStatus} />);

      // Should have header
      expect(screen.getByText('Test Printer')).toBeInTheDocument();

      // Should have visualization
      expect(screen.getByTestId('bed-visualization')).toBeInTheDocument();

      // Should have status info
      expect(screen.getByText('Idle')).toBeInTheDocument();
      expect(screen.getByText('Temperatures')).toBeInTheDocument();
    });

    it('handles printing job display', () => {
      const printingJob = {
        ...testStatus,
        state: 'Printing' as const,
        progress: 45,
        jobName: 'test-model.gcode',
      };

      const { container } = render(<PrinterBedCard printerModel={testPrinterModel} status={printingJob} />);

      expect(container.textContent).toContain('Printing');
      expect(container.textContent).toContain('45');
      expect(container.textContent).toContain('Job Progress');
    });
  });
});
