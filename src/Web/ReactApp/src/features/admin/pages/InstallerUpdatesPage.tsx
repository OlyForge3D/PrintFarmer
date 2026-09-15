import { useCallback } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Alert } from '@/common/components/ui';
import { InstallerUpdatesExperience } from '@/features/admin/components/InstallerUpdatesExperience';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { apiClient } from '@/services/api';
import type { SystemInfo } from '@/types/api';

export function InstallerUpdatesPage() {
  const { hasPermission } = useAuth();
  const canView = hasPermission('system_settings', 'admin');
  const query = useQuery({ queryKey: ['system-info', 'installer-updates'], queryFn: () => apiClient.getSystemInfo() as Promise<SystemInfo>, enabled: canView, refetchOnWindowFocus: true });
  const refetch = useCallback(() => { void query.refetch(); }, [query]);
  if (!canView) return <Alert type="error" title="Access denied">System settings administrator permission is required to view installation updates.</Alert>;
  if (query.isError) return <Alert type="warning" title="Update observation unknown">The installation snapshot could not be loaded. No update result is inferred; reconnect and retry to reconcile.</Alert>;
  return <InstallerUpdatesExperience inventory={query.data?.inventory} canExecute={hasPermission('updates', 'execute')} refetch={refetch} />;
}
