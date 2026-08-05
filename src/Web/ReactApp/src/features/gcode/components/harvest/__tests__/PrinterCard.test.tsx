import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { PrinterCard } from '../PrinterCard';
import {
  GcodeHarvestStatus,
  PrinterBackend,
  type GcodeHarvestOperation,
  type Printer,
} from '@/types/api';

const printer: Printer = {
  id: 'printer-1',
  name: 'Printer Alpha',
  manufacturerName: 'Prusa',
  modelName: 'MK4',
  backend: PrinterBackend.PrusaLink,
  backendUrl: 'http://printer.local',
  isReachable: true,
  isOnline: true,
  isEnabled: true,
  state: 'Idle',
};

const runningOperation: GcodeHarvestOperation = {
  id: 'operation-1',
  printerId: printer.id,
  printerName: printer.name,
  startedAt: new Date().toISOString(),
  status: GcodeHarvestStatus.Running,
  filesFound: 10,
  filesProcessed: 2,
  filesAdded: 2,
  filesSkipped: 0,
  filesErrored: 0,
  duplicatesSkipped: 0,
  totalSizeBytes: 1024,
};

const completedOperation: GcodeHarvestOperation = {
  ...runningOperation,
  status: GcodeHarvestStatus.Completed,
  completedAt: new Date().toISOString(),
};

describe('PrinterCard', () => {
  it('uses the dedicated danger action contract for Cancel', () => {
    render(<PrinterCard printer={printer} operation={runningOperation} />);

    const cancelButton = screen.getByRole('button', {
      name: `Cancel harvest on ${printer.name}`,
    });
    expect(cancelButton).toHaveClass(
      'bg-[var(--pf-button-danger-bg)]',
      'hover:bg-[var(--pf-button-danger-hover)]',
      'text-[var(--pf-on-danger)]',
      'border-[var(--pf-button-danger-border)]',
      'hover:scale-105',
      'hover:shadow-md',
    );
    expect(cancelButton).not.toHaveClass(
      'bg-pf-error-bg',
      'text-pf-error-text',
      'hover:bg-pf-error-bg',
      'hover:bg-pf-error-hover',
      'text-[var(--pf-text-inverse)]',
    );
  });

  it('uses accessible secondary surfaces for Details actions', () => {
    const { rerender } = render(
      <PrinterCard printer={printer} operation={runningOperation} />,
    );

    const expectAccessibleDetailsSurface = () => {
      const detailsButton = screen.getByRole('button', {
        name: `View details for ${printer.name}`,
      });
      expect(detailsButton).toHaveClass(
        'bg-pf-bg-2',
        'hover:bg-pf-bg-1',
        'text-pf-text-primary',
        'hover:scale-105',
        'hover:shadow-md',
      );
      expect(detailsButton).not.toHaveClass(
        'bg-pf-text-tertiary',
        'hover:bg-pf-text-secondary',
        'text-white',
      );
    };

    expectAccessibleDetailsSurface();

    rerender(<PrinterCard printer={printer} operation={completedOperation} />);

    expectAccessibleDetailsSurface();
  });
});
