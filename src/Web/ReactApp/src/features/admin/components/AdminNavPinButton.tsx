import { Button } from '@/common/components/ui';
import { useAdminNavPins } from '@/common/contexts/useAdminNavPins';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { canAccessDestination, getDestinationById } from '@/features/admin/registry/adminDestinations';

interface AdminNavPinButtonProps {
  destinationId: string;
}

export function AdminNavPinButton({ destinationId }: AdminNavPinButtonProps) {
  const auth = useAuth();
  const { pinnedIds, setPinned } = useAdminNavPins();
  const destination = getDestinationById(destinationId);

  if (!destination || destination.kind === 'hub' || auth.isLoading || !auth.isAuthenticated || !canAccessDestination(destination, auth)) {
    return null;
  }

  const pinned = pinnedIds.includes(destinationId);

  return (
    <Button
      type="button"
      variant={pinned ? 'secondary' : 'subtle'}
      size="sm"
      aria-pressed={pinned}
      aria-label={`${pinned ? 'Unpin' : 'Pin'} ${destination.label} from navbar`}
      onClick={() => setPinned(destinationId, !pinned)}
    >
      {pinned ? 'Pinned' : 'Pin'}
    </Button>
  );
}
