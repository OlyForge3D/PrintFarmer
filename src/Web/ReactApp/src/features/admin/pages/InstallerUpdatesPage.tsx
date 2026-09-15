import { useCallback, useEffect } from "react";
import { useQuery } from "@tanstack/react-query";
import { Alert, Button } from "@/common/components/ui";
import { InstallerUpdatesExperience } from "@/features/admin/components/InstallerUpdatesExperience";
import { useAuth } from "@/features/auth/hooks/useAuth";
import { apiClient } from "@/services/api";
import type { SystemInfo } from "@/types/api";

export function InstallerUpdatesPage() {
  const { hasPermission } = useAuth();
  const canView = hasPermission("system_settings", "admin");
  const {
    data,
    isError,
    isLoading,
    refetch: refetchInventory,
  } = useQuery<SystemInfo>({
    queryKey: ["system-info", "installer-updates"],
    queryFn: () => apiClient.getSystemInfo(),
    enabled: canView,
    refetchOnWindowFocus: true,
  });
  const refetch = useCallback(() => {
    void refetchInventory();
  }, [refetchInventory]);

  useEffect(() => {
    if (!canView) return;
    window.addEventListener("online", refetch);
    return () => window.removeEventListener("online", refetch);
  }, [canView, refetch]);

  if (!canView)
    return (
      <Alert type="error" title="Access denied">
        System settings administrator permission is required to view
        installation updates.
      </Alert>
    );
  if (isLoading) return <p role="status">Loading installation observation…</p>;
  if (isError)
    return (
      <div className="space-y-2">
        <Alert type="warning" title="Update observation unknown">
          The installation snapshot could not be loaded. No update result is
          inferred; reconnect and retry to reconcile.
        </Alert>
        <Button type="button" variant="secondary" onClick={refetch}>
          Retry installation observation
        </Button>
      </div>
    );

  // `updates:execute` has no backend authorization contract yet. A safe admin/view check
  // may display this read-only surface, but it must not imply a runtime permission grant.
  return (
    <InstallerUpdatesExperience inventory={data?.inventory} refetch={refetch} />
  );
}
