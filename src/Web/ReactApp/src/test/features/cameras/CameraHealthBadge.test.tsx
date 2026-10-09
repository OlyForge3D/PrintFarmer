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

    expect(screen.getByText('Preview failed · probe healthy')).toBeInTheDocument();
    expect(screen.getByTitle(
      "Camera probe health is checked periodically by the backend; preview status reflects this browser's image load."
    )).toContainElement(screen.getByText('Preview failed · probe healthy'));
    expect(screen.queryByText('Healthy')).not.toBeInTheDocument();
  });

  it('labels the healthy state as a periodic probe when the preview is not failed', () => {
    render(<CameraHealthBadge healthStatus={CameraHealthStatus.Healthy} />);

    expect(screen.getByText('Probe healthy')).toBeInTheDocument();
  });
});
