import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import React from 'react';

interface MockProgressDto {
  sessionId: string;
  networkRanges?: string[];
  autoDetectedNetworks?: boolean;
}

interface MockProgressProps {
  progress: MockProgressDto;
}

const MockProgress: React.FC<MockProgressProps> = ({ progress }) => (
  <div>
    <div data-testid="session">{progress.sessionId}</div>
    {progress.networkRanges && <div data-testid="networks">{progress.networkRanges.join(',')}</div>}
    {progress.autoDetectedNetworks && <div data-testid="auto">auto</div>}
  </div>
);

describe('Discovery progress extended fields', () => {
  it('renders session and network ranges', () => {
    const progress = {
      sessionId: 'sess-x',
      networkRanges: ['192.168.1.0/24','10.0.0.0/24'],
      autoDetectedNetworks: true
    };
    const { getByTestId } = render(<MockProgress progress={progress} />);
    expect(getByTestId('session').textContent).toBe('sess-x');
    expect(getByTestId('networks').textContent).toBe('192.168.1.0/24,10.0.0.0/24');
    expect(getByTestId('auto').textContent).toBe('auto');
  });
});
