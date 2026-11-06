import { useState, type ReactNode } from 'react';
import { Alert, HeaderActions } from '@/common/components/ui';
import { SettingsHeaderSlotContext } from '@/features/settings/components/settingsHeaderSlotContext';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { AdminNavPinButton } from '@/features/admin/components/AdminNavPinButton';
import { AdminPageShell } from '@/features/admin/components/AdminPageShell';
import { canAccessDestination, getDestinationById } from '@/features/admin/registry/adminDestinations';

interface AdminDestinationRouteProps {
  destinationId: string;
  children: ReactNode;
}

/** Keep the query subtree unmounted until the registry's access rule passes. */
export function AdminDestinationRoute({ destinationId, children }: AdminDestinationRouteProps) {
  const [headerSlot, setHeaderSlot] = useState<HTMLDivElement | null>(null);
  const auth = useAuth();
  const destination = getDestinationById(destinationId);
  if (!destination) throw new Error(`Unknown admin destination: ${destinationId}`);

  return (
    <AdminPageShell
      title={destination.label}
      subtitle={destination.description}
      icon={destination.icon}
      actions={
        <HeaderActions aria-label={`${destination.label} actions`} className="justify-end mb-0">
          <AdminNavPinButton destinationId={destinationId} />
          <div ref={setHeaderSlot} className="contents" />
        </HeaderActions>
      }
    >
      <SettingsHeaderSlotContext value={headerSlot}>
      {auth.isLoading ? (
        <p role="status">Loading access...</p>
      ) : auth.isAuthenticated && canAccessDestination(destination, auth) ? children : (
        <Alert type="error" title="Access Denied">
          You don't have permission to access this page.
        </Alert>
      )}
      </SettingsHeaderSlotContext>
    </AdminPageShell>
  );
}
