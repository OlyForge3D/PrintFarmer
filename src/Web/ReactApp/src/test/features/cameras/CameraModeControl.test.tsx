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
    for (const button of [snapshotButton, streamButton]) {
      expect(button).toHaveAttribute('title', button.getAttribute('aria-label'));
      expect(button.textContent).toBe('');
      expect(button.querySelector('svg')).not.toBeNull();
      expect(button).toHaveClass('h-8', 'w-8', 'p-0');
    }

    fireEvent.click(streamButton);
    expect(onModeChange).toHaveBeenCalledWith('stream');
    fireEvent.click(snapshotButton);
    expect(onModeChange).toHaveBeenCalledWith('snapshot');
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

    expect(screen.getByRole('status', { name: 'x400 Camera: Snapshot only' })).toHaveClass('sr-only');
    expect(screen.queryByRole('button', { name: 'Stream mode' })).not.toBeInTheDocument();
  });

  it('keeps stream-only capability information off the visible toolbar without inventing snapshot controls', () => {
    render(
      <CameraModeControl
        cameraName="x400 Camera"
        cameraMode="stream"
        hasStream
        hasSnapshot={false}
        onModeChange={vi.fn()}
      />
    );
    expect(screen.getByRole('status', { name: 'x400 Camera: Live stream only' })).toHaveClass('sr-only');
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
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
