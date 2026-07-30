import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import React from 'react';
import { DiscoveryStatus, type DiscoveryProgressDto } from '@/types/api';

interface MockProgressProps {
  progress: DiscoveryProgressDto;
}

const MockProgress: React.FC<MockProgressProps> = ({ progress }) => (
  <div>
    <div data-testid="session">{progress.sessionId}</div>
    <div data-testid="progress">{progress.scannedIps}/{progress.totalIps}</div>
    <div data-testid="message">{progress.message}</div>
    {progress.autoDetectedNetworks && <div data-testid="auto">auto</div>}
  </div>
);

describe('Discovery progress fields', () => {
  it('renders redacted progress without network targets', () => {
    const progress = {
      sessionId: 'sess-x',
      totalIps: 256,
      scannedIps: 64,
      printersFound: 1,
      printersExcluded: 0,
      progressPercentage: 25,
      status: DiscoveryStatus.Scanning,
      message: 'Scanning for compatible printers',
      autoDetectedNetworks: true,
    } satisfies DiscoveryProgressDto;
    const { getByTestId } = render(<MockProgress progress={progress} />);
    expect(getByTestId('session').textContent).toBe('sess-x');
    expect(getByTestId('progress').textContent).toBe('64/256');
    expect(getByTestId('message').textContent).not.toMatch(/(?:\d{1,3}\.){3}\d{1,3}/);
    expect(getByTestId('auto').textContent).toBe('auto');
  });
});
