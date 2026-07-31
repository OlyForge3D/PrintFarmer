import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { AdminStatTile } from '../AdminStatTile';

describe('AdminStatTile', () => {
  it('exposes a labelled group with the subsystem name', () => {
    render(<AdminStatTile label="Database" badge="Healthy" badgeVariant="success" />);
    expect(screen.getByRole('group', { name: 'Database' })).toBeInTheDocument();
    expect(screen.getByText('Healthy')).toBeInTheDocument();
  });

  it('prefers an explicit aria-label over the visible label', () => {
    render(<AdminStatTile label="Database" ariaLabel="Database: healthy" />);
    expect(screen.getByRole('group', { name: 'Database: healthy' })).toBeInTheDocument();
  });

  it('omits the value line entirely when no measurement is supplied', () => {
    // Most subsystems report prose, not a number. Forcing an empty mono line
    // would leave a ragged gap under every tile that has nothing to measure.
    const { container } = render(<AdminStatTile label="SignalR" detail="Hub accessible" />);
    expect(container.querySelector('.font-pf-mono')).toBeNull();
    expect(screen.getByText('Hub accessible')).toBeInTheDocument();
  });

  it('sets a supplied measurement in the mono face with tabular figures', () => {
    const { container } = render(<AdminStatTile label="API" value="12 ms" />);
    const value = container.querySelector('.font-pf-mono');
    expect(value).not.toBeNull();
    expect(value).toHaveTextContent('12 ms');
    expect(value?.className).toContain('tabular-nums');
  });

  it('keeps prose out of the mono face when both value and detail are present', () => {
    const { container } = render(
      <AdminStatTile label="Disk" value="214 GB" detail="of 1.82 TB allocated" />,
    );
    const mono = container.querySelector('.font-pf-mono');
    expect(mono).toHaveTextContent('214 GB');
    expect(mono).not.toHaveTextContent('allocated');
  });

  it('forwards data attributes so pages can target specific tiles', () => {
    render(
      <AdminStatTile
        label="Database"
        dataAttributes={{ 'data-testid': 'tile', 'data-subsystem-key': 'database' }}
      />,
    );
    expect(screen.getByTestId('tile')).toHaveAttribute('data-subsystem-key', 'database');
  });

  it('carries status in the badge and the border, not colour alone', () => {
    render(
      <AdminStatTile
        label="Printers"
        badge="Degraded"
        badgeVariant="warning"
        borderClassName="border-pf-warning"
        dataAttributes={{ 'data-testid': 'tile' }}
      />,
    );
    expect(screen.getByText('Degraded')).toBeInTheDocument();
    expect(screen.getByTestId('tile').className).toContain('border-pf-warning');
  });
});
