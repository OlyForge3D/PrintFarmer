import { CameraHealthStatus } from '@/types/api';

export interface CameraAttentionContent {
  title: string;
  issue: string;
  action: string;
  tone: 'info' | 'warning' | 'error';
}

interface CameraAttentionOptions {
  healthStatus: CameraHealthStatus;
  healthMessage?: string;
  hasStream: boolean;
  hasSnapshot: boolean;
  imageError: boolean;
  cameraMode: 'stream' | 'snapshot';
}

function ensureSentence(value: string | undefined | null, fallback: string): string {
  const trimmed = value?.trim();
  if (!trimmed) {
    return fallback;
  }

  return /[.!?]$/.test(trimmed) ? trimmed : `${trimmed}.`;
}

function inferHealthAction(
  healthStatus: CameraHealthStatus,
  healthMessage: string | undefined,
  hasStream: boolean,
  hasSnapshot: boolean
): string {
  const normalizedMessage = healthMessage?.toLowerCase() ?? '';

  if (normalizedMessage.includes('snapshot')) {
    return hasStream
      ? 'Switch to live stream now, then verify the saved snapshot URL.'
      : 'Verify the saved snapshot URL and camera service.';
  }

  if (
    normalizedMessage.includes('stream')
    || normalizedMessage.includes('mjpg')
    || normalizedMessage.includes('webrtc')
  ) {
    return hasSnapshot
      ? 'Switch to snapshot preview now, then verify the saved stream URL.'
      : 'Verify the saved stream URL and camera service.';
  }

  if (
    normalizedMessage.includes('timeout')
    || normalizedMessage.includes('timed out')
    || normalizedMessage.includes('unreachable')
    || normalizedMessage.includes('connect')
    || normalizedMessage.includes('network')
    || normalizedMessage.includes('dns')
  ) {
    return 'Check the camera power/network path and confirm the URL responds.';
  }

  if (
    normalizedMessage.includes('unauthorized')
    || normalizedMessage.includes('forbidden')
    || normalizedMessage.includes('401')
    || normalizedMessage.includes('403')
    || normalizedMessage.includes('auth')
  ) {
    return 'Update the saved camera credentials or authenticated URL.';
  }

  if (healthStatus === CameraHealthStatus.Unhealthy) {
    return 'Open the feed in a new tab. If it still fails, verify the camera URL, power, and network connection.';
  }

  return 'Refresh the feed. If it still degrades, verify the camera URL and network path.';
}

export function getCameraAttentionContent({
  healthStatus,
  healthMessage,
  hasStream,
  hasSnapshot,
  imageError,
  cameraMode,
}: CameraAttentionOptions): CameraAttentionContent | null {
  if (!hasStream && !hasSnapshot) {
    return {
      title: 'Feed setup required',
      issue: 'No stream or snapshot URL is configured for this camera.',
      action: 'Add a stream or snapshot URL in Manage Cameras.',
      tone: 'warning',
    };
  }

  if (imageError) {
    return {
      title: cameraMode === 'stream' ? 'Live stream failed' : 'Snapshot preview failed',
      issue: cameraMode === 'stream'
        ? 'This card could not load the live stream.'
        : 'This card could not load the snapshot preview.',
      action: cameraMode === 'stream'
        ? hasSnapshot
          ? 'Switch to snapshot preview or open the feed in a new tab. If both fail, verify the stream URL.'
          : 'Open the feed in a new tab and verify the saved stream URL.'
        : hasStream
          ? 'Switch to live stream or open the feed in a new tab. If both fail, verify the snapshot URL.'
          : 'Open the feed in a new tab and verify the saved snapshot URL.',
      tone: 'error',
    };
  }

  if (healthStatus === CameraHealthStatus.Unhealthy) {
    return {
      title: 'Camera health check failed',
      issue: ensureSentence(healthMessage, 'The last health check could not confirm a working camera feed.'),
      action: inferHealthAction(healthStatus, healthMessage, hasStream, hasSnapshot),
      tone: 'error',
    };
  }

  if (healthStatus === CameraHealthStatus.Degraded) {
    return {
      title: 'Camera feed is degraded',
      issue: ensureSentence(healthMessage, 'The camera feed is responding inconsistently.'),
      action: inferHealthAction(healthStatus, healthMessage, hasStream, hasSnapshot),
      tone: 'warning',
    };
  }

  return null;
}
