import { render } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { MmuControlBox } from '../MmuControlBox';
import { MmuGateStatus, type MmuStatus } from '@/types/api';

const mmuStatus: MmuStatus = {
  enabled: true,
  isHomed: true,
  activeTool: 0,
  activeGate: 0,
  filamentState: 'Loaded',
  action: 'Idle',
  numGates: 1,
  hasBypass: false,
  endlessSpool: false,
  clogDetection: false,
  gates: [
    {
      index: 0,
      status: MmuGateStatus.Available,
      material: 'PLA',
      color: '#ff0000',
      filamentName: 'Test PLA',
      spoolId: 1,
    },
  ],
};

describe('MmuControlBox', () => {
  it('uses the live inset-surface token for every spool hub', () => {
    const { container } = render(
      <MmuControlBox printerId="printer-1" mmuStatus={mmuStatus} isOnline />,
    );

    const hubs = container.querySelectorAll('ellipse[cx="28"][cy="30"]');
    expect(hubs.length).toBeGreaterThan(0);
    for (const hub of hubs) {
      expect(hub).toHaveAttribute('fill', 'var(--pf-bg-2)');
    }
  });
});
