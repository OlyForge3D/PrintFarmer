import { useSystemCapabilities } from '@/common/hooks/useSystemCapabilities';
import { Alert } from '@/common/components/ui';

/**
 * Displays a non-dismissible info banner when the platform has degraded
 * capabilities (e.g. ARM64 where slicing / 3D model features are disabled).
 * Renders nothing on x64 or while the capabilities query is still loading.
 */
export function PlatformBanner() {
  const { data: capabilities } = useSystemCapabilities();

  if (!capabilities?.platformNote) return null;

  return (
    <Alert type="info" title="Platform Notice">
      {capabilities.platformNote}
    </Alert>
  );
}
