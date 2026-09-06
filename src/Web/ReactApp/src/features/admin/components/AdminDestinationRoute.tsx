import type { ReactNode } from 'react';
import { Alert } from '@/common/components/ui';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { AdminPageShell } from '@/features/admin/components/AdminPageShell';
import { canAccessDestination, getDestinationById } from '@/features/admin/registry/adminDestinations';

interface AdminDestinationRouteProps {
  destinationId: string;
  children: ReactNode;
}

/** Keep the query subtree unmounted until the registry's access rule passes. */
export function AdminDestinationRoute({ destinationId, children }: AdminDestinationRouteProps) {
  const auth = useAuth();
  const destination = getDestinationById(destinationId);
  if (!destination) throw new Error(`Unknown admin destination: ${destinationId}`);

  return (
    <AdminPageShell title={destination.label} subtitle={destination.description} icon={destination.icon}>
      {auth.isLoading ? (
        <p role="status">Loading access...</p>
      ) : auth.isAuthenticated && canAccessDestination(destination, auth) ? children : (
        <Alert type="error" title="Access Denied">
          You don't have permission to access this page.
        </Alert>
      )}
    </AdminPageShell>
  );
}
