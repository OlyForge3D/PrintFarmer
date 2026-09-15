import { useCallback, useEffect, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Alert, Button } from "@/common/components/ui";
import { InstallerUpdatesExperience } from "@/features/admin/components/InstallerUpdatesExperience";
import { useAuth } from "@/features/auth/hooks/useAuth";
import { apiClient } from "@/services/api";
import type { SystemInfo } from "@/types/api";

type ConnectionObservation = "connected" | "unknown";

export function InstallerUpdatesPage() {
  const [observation, setObservation] = useState<ConnectionObservation>(
    navigator.onLine ? "connected" : "unknown",
  );
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
    // This page owns reconnect reconciliation through its single explicit listener.
    refetchOnReconnect: false,
  });
  const refetch = useCallback(async () => {
    try {
      const result = await refetchInventory();
      setObservation(result.isError ? "unknown" : "connected");
    } catch {
      // Keep the observation unknown until a successful explicit retry.
      setObservation("unknown");
    }
  }, [refetchInventory]);

  useEffect(() => {
    if (!canView) return;
    const reconnect = () => { void refetch(); };
    const disconnect = () => setObservation("unknown");
    window.addEventListener("online", reconnect);
    window.addEventListener("offline", disconnect);
    return () => {
      window.removeEventListener("online", reconnect);
      window.removeEventListener("offline", disconnect);
    };
  }, [canView, refetch]);

  if (!canView)
    return (
      <Alert type="error" title="Access denied">
        System settings administrator permission is required to view
        installation updates.
      </Alert>
    );
  // A paused initial query has no observation. Do not leave an offline admin at
  // a loading message that cannot resolve until the browser reconnects.
  if (isError || (observation === "unknown" && !data))
    return (
      <div className="space-y-2" role="status" aria-live="polite" aria-label="Update observation unknown">
        <Alert type="warning" title="Update observation unknown">
          The installation snapshot is unknown. No update result is inferred;
          reconnect and retry to reconcile the host observation.
        </Alert>
        <Button type="button" variant="secondary" onClick={refetch}>
          Retry installation observation
        </Button>
      </div>
    );
  if (isLoading) return <p role="status">Loading installation observation...</p>;

  // `updates:execute` has no backend authorization contract yet. A safe admin/view check
  // may display this read-only surface, but it must not imply a runtime permission grant.
  return (
    <InstallerUpdatesExperience inventory={data?.inventory} observation={observation} />
  );
}
