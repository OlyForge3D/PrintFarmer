import '@testing-library/jest-dom';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { CalibrationSetupPrompt } from '../CalibrationSetupPrompt';

const hoisted = vi.hoisted(() => ({
  useCalibrationCandidateFromFleet: vi.fn(),
}));

vi.mock('@/features/printers/hooks/useCalibrationCandidatesFleet', () => ({
  useCalibrationCandidateFromFleet: hoisted.useCalibrationCandidateFromFleet,
}));

vi.mock('@/features/printers/components/CalibrationSetupModal', () => ({
  CalibrationSetupModal: ({ isOpen, printerId }: { isOpen: boolean; printerId: string }) =>
    isOpen ? <div data-testid="calibration-setup-modal">Modal for {printerId}</div> : null,
}));

function baseCandidate(overrides: Record<string, unknown> = {}) {
  return {
    id: 'printer-1',
    name: 'Printer One',
    eligible: false,
    missingInputs: [],
    rejectionReasons: [],
    activeToolheadIndex: null,
    excludedRegions: null,
    firmware: { family: 'Unknown', gcodeDialect: 'Unknown', detectionSource: 'unknown', verified: false },
    slicer: null,
    toolheads: [{ id: 't1', index: 0, isPrimary: true, offset: {} }],
    ...overrides,
  };
}

describe('CalibrationSetupPrompt (#1923)', () => {
  beforeEach(() => {
    hoisted.useCalibrationCandidateFromFleet.mockReset();
  });

  it('renders nothing while fleet data is still loading', () => {
    hoisted.useCalibrationCandidateFromFleet.mockReturnValue({ data: undefined });
    const { container } = render(
      <CalibrationSetupPrompt printerId="printer-1" printerName="Printer One" />,
    );
    expect(container).toBeEmptyDOMElement();
  });

  it('renders nothing for an already-eligible printer', () => {
    hoisted.useCalibrationCandidateFromFleet.mockReturnValue({
      data: baseCandidate({ eligible: true }),
    });
    const { container } = render(
      <CalibrationSetupPrompt printerId="printer-1" printerName="Printer One" />,
    );
    expect(container).toBeEmptyDOMElement();
  });

  it('shows a "Set up calibration" onboarding prompt when nothing has been configured, styled as an invitation rather than an error (#1923)', () => {
    hoisted.useCalibrationCandidateFromFleet.mockReturnValue({ data: baseCandidate() });
    render(<CalibrationSetupPrompt printerId="printer-1" printerName="Printer One" />);
    const button = screen.getByRole('button', { name: /set up calibration for printer one/i });
    expect(button).toBeInTheDocument();
    // Not-started reads as an inviting "come try this" nudge — blue, not red/error.
    expect(button.className).toMatch(/bg-blue-500/);
    expect(button.className).not.toMatch(/red|error|danger/i);
    expect(button).not.toHaveAttribute('role', 'alert');
    expect(button.title).toMatch(/hasn.t been set up yet/i);
    expect(button.title).not.toMatch(/error|fail|invalid|danger/i);
  });

  it('shows a distinct "Finish calibration setup" prompt when partially configured, styled differently from "not started" (#1923)', () => {
    hoisted.useCalibrationCandidateFromFleet.mockReturnValue({
      data: baseCandidate({ activeToolheadIndex: 0 }),
    });
    render(<CalibrationSetupPrompt printerId="printer-1" printerName="Printer One" />);
    const button = screen.getByRole('button', { name: /finish calibration setup for printer one/i });
    expect(button).toBeInTheDocument();
    // Partial reads as "you're part way there" — amber, still not red/error, and
    // visually distinct from the not-started (blue) tone so the two states are
    // distinguishable at a glance without opening printer details.
    expect(button.className).toMatch(/bg-amber-500/);
    expect(button.className).not.toMatch(/bg-blue-500/);
    expect(button.className).not.toMatch(/red|error|danger/i);
    expect(button).not.toHaveAttribute('role', 'alert');
    expect(button.title).toMatch(/partially complete/i);
    expect(button.title).not.toMatch(/error|fail|invalid|danger/i);
  });

  it('opens the calibration setup modal for this printer when clicked', () => {
    hoisted.useCalibrationCandidateFromFleet.mockReturnValue({ data: baseCandidate() });
    render(<CalibrationSetupPrompt printerId="printer-1" printerName="Printer One" />);

    expect(screen.queryByTestId('calibration-setup-modal')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /set up calibration for printer one/i }));
    expect(screen.getByTestId('calibration-setup-modal')).toHaveTextContent('Modal for printer-1');
  });
});
