import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { HarvestWizardStep1Selection } from '../steps/HarvestWizardStep1Selection';
import { PrinterBackend, type Printer } from '@/types/api';

// Regression coverage for issue #2377: while activeHarvests is still being
// fetched (isLoadingActiveHarvests), printer selection must be disabled so a
// user cannot start a harvest that conflicts with one already running.

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

describe('HarvestWizardStep1Selection', () => {
  it('disables printer selection while active harvests are still loading', async () => {
    const user = userEvent.setup();
    const onSelect = vi.fn();

    render(
      <HarvestWizardStep1Selection
        printers={[printer]}
        selectedPrinterId={null}
        onSelect={onSelect}
        activeHarvests={[]}
        isLoadingActiveHarvests
      />,
    );

    expect(screen.getByRole('status')).toHaveTextContent('Checking for active harvests');

    const printerButton = screen.getByRole('button', { name: new RegExp(printer.name) });
    expect(printerButton).toBeDisabled();

    await user.click(printerButton);
    expect(onSelect).not.toHaveBeenCalled();
  });

  it('allows printer selection once active harvests have loaded', async () => {
    const user = userEvent.setup();
    const onSelect = vi.fn();

    render(
      <HarvestWizardStep1Selection
        printers={[printer]}
        selectedPrinterId={null}
        onSelect={onSelect}
        activeHarvests={[]}
        isLoadingActiveHarvests={false}
      />,
    );

    expect(screen.queryByRole('status')).not.toBeInTheDocument();

    const printerButton = screen.getByRole('button', { name: new RegExp(printer.name) });
    expect(printerButton).toBeEnabled();

    await user.click(printerButton);
    expect(onSelect).toHaveBeenCalledWith(printer.id);
  });
});
