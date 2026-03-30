import { describe, expect, it } from 'vitest';
import { CameraHealthStatus } from '@/types/api';
import { getCameraAttentionContent } from '@/features/cameras/utils/cameraAttention';

describe('cameraAttention', () => {
  it('tells the user to add URLs when a camera has no feed configured', () => {
    expect(
      getCameraAttentionContent({
        healthStatus: CameraHealthStatus.Unknown,
        hasStream: false,
        hasSnapshot: false,
        imageError: false,
        cameraMode: 'snapshot',
      })
    ).toEqual({
      title: 'Feed setup required',
      issue: 'No stream or snapshot URL is configured for this camera.',
      action: 'Add a stream or snapshot URL in Manage Cameras.',
      tone: 'warning',
    });
  });

  it('explains how to recover from a failed snapshot preview', () => {
    expect(
      getCameraAttentionContent({
        healthStatus: CameraHealthStatus.Healthy,
        hasStream: true,
        hasSnapshot: true,
        imageError: true,
        cameraMode: 'snapshot',
      })
    ).toEqual({
      title: 'Snapshot preview failed',
      issue: 'This card could not load the snapshot preview.',
      action: 'Switch to live stream or open the feed in a new tab. If both fail, verify the snapshot URL.',
      tone: 'error',
    });
  });

  it('uses the backend health message and gives a snapshot-specific action', () => {
    expect(
      getCameraAttentionContent({
        healthStatus: CameraHealthStatus.Degraded,
        healthMessage: 'Snapshot endpoint timed out during the last health check',
        hasStream: true,
        hasSnapshot: true,
        imageError: false,
        cameraMode: 'snapshot',
      })
    ).toEqual({
      title: 'Camera feed is degraded',
      issue: 'Snapshot endpoint timed out during the last health check.',
      action: 'Switch to live stream now, then verify the saved snapshot URL.',
      tone: 'warning',
    });
  });

  it('gives a credential-focused action for unhealthy cameras', () => {
    expect(
      getCameraAttentionContent({
        healthStatus: CameraHealthStatus.Unhealthy,
        healthMessage: 'Camera returned 401 Unauthorized during the health check',
        hasStream: true,
        hasSnapshot: false,
        imageError: false,
        cameraMode: 'stream',
      })
    ).toEqual({
      title: 'Camera health check failed',
      issue: 'Camera returned 401 Unauthorized during the health check.',
      action: 'Update the saved camera credentials or authenticated URL.',
      tone: 'error',
    });
  });

  it('returns null for healthy cameras with a working preview', () => {
    expect(
      getCameraAttentionContent({
        healthStatus: CameraHealthStatus.Healthy,
        hasStream: true,
        hasSnapshot: true,
        imageError: false,
        cameraMode: 'stream',
      })
    ).toBeNull();
  });
});
