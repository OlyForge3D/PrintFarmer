import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { CameraModeControl } from '@/features/cameras/components/CameraModeControl';

describe('CameraModeControl', () => {
  it('renders accessible controls for a camera with both supported modes', () => {
    const onModeChange = vi.fn();
    render(
      <CameraModeControl
        cameraName="x400 Camera"
        cameraMode="snapshot"
        hasStream
        hasSnapshot
        onModeChange={onModeChange}
      />
    );

    expect(screen.getByRole('group', { name: 'x400 Camera preview mode' })).toBeInTheDocument();
    const snapshotButton = screen.getByRole('button', { name: 'Snapshot mode' });
    const streamButton = screen.getByRole('button', { name: 'Stream mode' });
    expect(snapshotButton).toHaveAttribute('aria-pressed', 'true');
    expect(streamButton).toHaveAttribute('aria-pressed', 'false');

    fireEvent.click(streamButton);
    expect(onModeChange).toHaveBeenCalledWith('stream');
  });

  it('describes a snapshot-only camera without presenting a disabled or fake stream control', () => {
    render(
      <CameraModeControl
        cameraName="x400 Camera"
        cameraMode="snapshot"
        hasStream={false}
        hasSnapshot
        onModeChange={vi.fn()}
      />
    );

    expect(screen.getByRole('status', { name: 'x400 Camera: Snapshot only' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Stream mode' })).not.toBeInTheDocument();
  });

  it('explains when a configured stream format cannot be previewed', () => {
    render(
      <CameraModeControl
        cameraName="x400 Camera"
        cameraMode="snapshot"
        hasStream={false}
        hasSnapshot
        streamUnavailable
        onModeChange={vi.fn()}
      />
    );

    expect(
      screen.getByRole('status', { name: 'x400 Camera: Snapshot only · live stream unsupported' })
    ).toBeInTheDocument();
  });
});
