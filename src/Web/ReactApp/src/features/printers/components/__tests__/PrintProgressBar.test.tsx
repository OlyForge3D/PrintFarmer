import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { PrintProgressBar } from '@/features/printers/components/PrintProgressBar';

describe('PrintProgressBar temperature row', () => {
  it('keeps offline temperature readouts without displaying stale readings or targets', () => {
    const { rerender } = render(
      <PrintProgressBar progress={42} jobName="benchy.gcode" isActive showTemperatures
        isOnline hotendTemp={210} hotendTarget={220} bedTemp={60} bedTarget={65} />,
    );
    expect(screen.getByTitle('Hotend temperature')).toHaveTextContent('210°');
    expect(screen.getByTitle('Bed temperature')).toHaveTextContent('60°');

    rerender(
      <PrintProgressBar progress={42} jobName="benchy.gcode" isActive={false} showInactiveState
        showTemperatures isOnline={false} hotendTemp={210} hotendTarget={220} bedTemp={60} bedTarget={65} />,
    );
    expect(screen.getByTitle('Hotend temperature')).toHaveTextContent('--°');
    expect(screen.getByTitle('Bed temperature')).toHaveTextContent('--°');
    expect(screen.queryByText(/210°|220°|60°|65°/)).not.toBeInTheDocument();
    expect(screen.getByText('No active print')).toBeVisible();
  });

  it('does not add a temperature row to detailed progress bars', () => {
    render(<PrintProgressBar progress={0} jobName={null} isActive={false} />);
    expect(screen.queryByTitle('Hotend temperature')).not.toBeInTheDocument();
    expect(screen.queryByTitle('Bed temperature')).not.toBeInTheDocument();
  });
});
