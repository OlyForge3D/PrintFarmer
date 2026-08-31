import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { PrinterSlicerSelector } from '@/features/slicer/components/job/PrinterSlicerSelector';

describe('PrinterSlicerSelector', () => {
  it('anchors the change affordance at the far right without consuming a content row', () => {
    render(
      <PrinterSlicerSelector
        printers={[{
          id: 'printer-1',
          name: 'Workshop MK4',
          modelName: 'MK4',
          isOnline: true,
        }]}
        selectedPrinterId="printer-1"
        onPrinterChange={vi.fn()}
      />,
    );

    const trigger = screen.getByRole('button', { name: /Workshop MK4.*Change/i });
    const affordance = screen.getByTestId('printer-change-affordance');

    const changeLabel = screen.getByText('Change');

    expect(trigger).toHaveClass('relative', 'pr-10!', 'sm:pr-28!');
    expect(affordance).toHaveClass('absolute', 'right-3', 'top-1/2');
    expect(changeLabel).toHaveClass('hidden', 'sm:inline');
    expect(trigger).toContainElement(affordance);
    expect(affordance).toContainElement(changeLabel);
  });
});
