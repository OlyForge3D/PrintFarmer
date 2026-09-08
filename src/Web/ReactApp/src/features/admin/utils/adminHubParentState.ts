import type { PageParent } from '@/common/components/PageTemplate';
import { useLocation } from 'react-router';
import { ADMIN_HUB_PARENT } from '@/features/admin/registry';

const ADMIN_HUB_PARENT_STATE_TOKEN = 'admin-control-center';

export const ADMIN_HUB_ROUTE_STATE = {
  pageParent: ADMIN_HUB_PARENT_STATE_TOKEN,
} as const;

function hasAdminHubRouteState(
  state: unknown,
): state is typeof ADMIN_HUB_ROUTE_STATE {
  if (!state || typeof state !== 'object') {
    return false;
  }

  return (state as { pageParent?: unknown }).pageParent === ADMIN_HUB_PARENT_STATE_TOKEN;
}

export function useAdminHubParent(): PageParent | undefined {
  const location = useLocation();

  return hasAdminHubRouteState(location.state)
    ? ADMIN_HUB_PARENT
    : undefined;
}
