import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { CameraHealthBadge } from '@/features/cameras/components/CameraHealthBadge';
import { CameraHealthStatus } from '@/types/api';

describe('CameraHealthBadge', () => {
  it('reports a failed browser preview separately from a successful backend probe', () => {
    render(
      <CameraHealthBadge
        healthStatus={CameraHealthStatus.Healthy}
        previewFailed
      />
    );

    expect(screen.getByText('Preview failed')).not.toHaveClass('sr-only');
    expect(screen.getByRole('status', { name: 'Preview failed · probe healthy' })).toBeInTheDocument();
    expect(screen.getByTitle(
      "Probe healthy. Camera probe health is checked periodically by the backend; preview status reflects this browser's image load."
    )).toContainElement(screen.getByText('Preview failed'));
    expect(screen.queryByText('Healthy')).not.toBeInTheDocument();
  });

  it.each(Object.values(CameraHealthStatus))('keeps %s probe metadata in an accessible compact icon', (healthStatus) => {
    render(<CameraHealthBadge healthStatus={healthStatus} />);

    const label = `Probe ${healthStatus.toLowerCase()}`;
    expect(screen.getByText(label)).toHaveClass('sr-only');
    expect(screen.getByRole('status', { name: label }).querySelector('svg')).not.toBeNull();
  });
});
