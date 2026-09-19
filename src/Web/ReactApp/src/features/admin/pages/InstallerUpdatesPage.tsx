import { useCallback, useEffect, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Alert, Button } from "@/common/components/ui";
import { InstallerUpdatesExperience } from "@/features/admin/components/InstallerUpdatesExperience";
import { useAuth } from "@/features/auth/hooks/useAuth";
import { apiClient } from "@/services/api";
import { UpdateChannelSaveRejectedError } from "@/features/admin/utils/updateChannelSaveErrors";
import type { SystemInfo, UpdateChannelSettings } from "@/types/api";

type ConnectionObservation = "connected" | "unknown";

export function InstallerUpdatesPage() {
  const [observation, setObservation] = useState<ConnectionObservation>(
    navigator.onLine ? "connected" : "unknown",
  );
  const observationGeneration = useRef(0);
  const [unknownSince, setUnknownSince] = useState(() => navigator.onLine ? 0 : Date.now());
  const markUnknown = useCallback(() => {
    setUnknownSince(Date.now());
    setObservation("unknown");
  }, []);
  const { hasPermission } = useAuth();
  const canView = hasPermission("system_settings", "admin");
  const {
    data,
    dataUpdatedAt,
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
  const {
    data: updateChannelSettings,
    isError: updateChannelIsError,
    isPending: updateChannelIsLoading,
    refetch: refetchUpdateChannel,
  } = useQuery<UpdateChannelSettings>({
    queryKey: ["settings", "UpdateChannel"],
    queryFn: () => apiClient.getUpdateChannelSettings(),
    enabled: canView,
  });

  // A later successful focus fetch has a newer query timestamp than the
  // offline observation, so it is fresh evidence while the browser is online.
  // This derived value avoids a synchronous state update from an effect.
  const effectiveObservation: ConnectionObservation =
    observation === "unknown" && navigator.onLine && dataUpdatedAt > unknownSince
      ? "connected"
      : observation;

  const refetch = useCallback(async () => {
    const requestGeneration = ++observationGeneration.current;
    try {
      const result = await refetchInventory();
      // An offline event increments the generation. Both that token and the
      // current browser state prevent its older in-flight request from reviving
      // a connected banner after the browser became offline.
      if (requestGeneration !== observationGeneration.current || !navigator.onLine) {
        if (!navigator.onLine) markUnknown();
        return;
      }
      setObservation(result.isError ? "unknown" : "connected");
    } catch {
      if (requestGeneration === observationGeneration.current) {
        markUnknown();
      }
    }
  }, [markUnknown, refetchInventory]);

  useEffect(() => {
    if (!canView) return;
    const reconnect = () => { void refetch(); };
    const disconnect = () => {
      observationGeneration.current += 1;
      markUnknown();
    };
    window.addEventListener("online", reconnect);
    window.addEventListener("offline", disconnect);
    return () => {
      window.removeEventListener("online", reconnect);
      window.removeEventListener("offline", disconnect);
    };
  }, [canView, markUnknown, refetch]);

  if (!canView)
    return (
      <Alert type="error" title="Access denied">
        System settings administrator permission is required to view
        installation updates.
      </Alert>
    );
  // A paused initial query has no observation. Do not leave an offline admin at
  // a loading message that cannot resolve until the browser reconnects.
  if (isError || (effectiveObservation === "unknown" && !data))
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
    <InstallerUpdatesExperience
      inventory={data?.inventory}
      updateScheduling={data?.updateScheduling}
      observation={effectiveObservation}
      updateChannelSettings={updateChannelSettings}
      updateChannelIsLoading={updateChannelIsLoading}
      updateChannelIsError={updateChannelIsError}
      onRetryUpdateChannel={async () => {
        // An explicit GET-only retry: the resolved (or thrown) value here is
        // the authoritative contract the child component reconciles from
        // directly, rather than inferring success from the
        // `updateChannelSettings` prop changing identity. TanStack Query's
        // structural sharing can return the exact same cached object
        // reference for a deep-equal refetch result, which would otherwise
        // never re-trigger a prop-identity effect.
        const result = await refetchUpdateChannel();
        if (result.isError || !result.data) {
          throw new Error("UpdateChannel settings could not be confirmed.");
        }
        return result.data;
      }}
      onSaveUpdateChannel={async (settings) => {
        // A POST rejection (including a timeout or lost response) is not by
        // itself authoritative. Always attempt the refetch below so the UI
        // reconciles to the server's real state instead of guessing from the
        // POST outcome alone.
        try {
          await apiClient.updateUpdateChannelSettings(settings);
        } catch {
          // Fall through to the refetch below regardless of this rejection.
        }
        const result = await refetchUpdateChannel();
        if (result.isError || !result.data) {
          // Neither the POST nor the refetch confirmed anything: the outcome
          // is genuinely unknown.
          throw new Error("UpdateChannel settings could not be confirmed after save.");
        }
        const authoritative = result.data;
        const matchesRequested =
          authoritative.channel === settings.channel &&
          authoritative.insiderAcknowledged === settings.insiderAcknowledged;
        if (!matchesRequested) {
          // The authoritative refetch is the source of truth: a mismatch
          // means the save was rejected or left unchanged, regardless of
          // whether the POST promise itself resolved or rejected.
          throw new UpdateChannelSaveRejectedError(authoritative);
        }
        void refetchInventory();
        return authoritative;
      }}
      onAuthorizeHostUpdate={() => apiClient.authorizeHostUpdate()}
      onExecuteHostUpdate={(authorizationId) =>
        apiClient.executeHostUpdate({ authorizationId })
      }
      onGetHostUpdateStatus={(releaseId) =>
        apiClient.getHostUpdateStatus(releaseId)
      }
      onRecoverHostUpdate={(releaseId, requestId) =>
        apiClient.recoverHostUpdate(releaseId, requestId)
      }
    />
  );
}
