import { Alert, Button, Card, Checkbox, FormField, Input, Select } from "@/common/components/ui";
import { Modal } from "@/common/components/modals/Modal";
import { UpdateChannelSaveRejectedError } from "@/features/admin/utils/updateChannelSaveErrors";
import { getErrorMessage, isApiError } from "@/common/utils/apiErrors";
import type {
  HostUpdateManualAuthorizationResponse,
  HostUpdateRecoveryResult,
  HostUpdateStatusResponse,
  ServiceInventory,
  UpdateChannel,
  UpdateChannelSettings,
  UpdateSchedulingStatus,
} from "@/types/api";
import { useEffect, useRef, useState } from "react";

const INSIDER_WARNING =
  "Insider updates may arrive more frequently and have reduced stability compared with stable releases.";
const UNKNOWN = "Unknown";
const MANUAL_DISABLED_REASON =
  "Update now is unavailable because the runtime execution contract, including its constrained executor, fresh host evidence, recovery, reauthentication, and request-origin protections, is not available.";
const AUTO_DISABLED_REASON =
  "Auto-update is off and unavailable because the runtime scheduler, bounded standing-permission, maintenance-window, recovery, and host-policy contract is not available.";
const MANUAL_UPDATE_RELEASE_KEY = "printfarmer.manual-host-update.release-id";

function readManualUpdateReleaseId() {
  try {
    return window.localStorage.getItem(MANUAL_UPDATE_RELEASE_KEY);
  } catch {
    return null;
  }
}

function writeManualUpdateReleaseId(releaseId: string) {
  try {
    window.localStorage.setItem(MANUAL_UPDATE_RELEASE_KEY, releaseId);
  } catch {
    // Persistence is best effort; execution must not depend on storage.
  }
}

function clearManualUpdateReleaseId() {
  try {
    window.localStorage.removeItem(MANUAL_UPDATE_RELEASE_KEY);
  } catch {
    // Persistence is best effort.
  }
}

function isRolledBackStatus(status: HostUpdateStatusResponse) {
  const activities = Array.isArray(status.activities) ? status.activities : [];
  const terminal = activities.at(-1);
  return status.currentState === "Completed" &&
    terminal?.phase === "recovery:rolled_back";
}

function isTerminalForRetry(status: HostUpdateStatusResponse) {
  return status.currentState === "Completed" || status.currentState === "RecoveryRequired";
}

function isTerminalForReleaseIdentity(status: HostUpdateStatusResponse) {
  return status.currentState === "Completed";
}

export interface InstallerUpdatesExperienceProps {
  inventory: ServiceInventory | null | undefined;
  updateScheduling?: UpdateSchedulingStatus | null;
  observation: ConnectionObservation;
  updateChannelSettings?: UpdateChannelSettings;
  updateChannelIsLoading?: boolean;
  updateChannelIsError?: boolean;
  onRetryUpdateChannel?: () => Promise<UpdateChannelSettings>;
  onSaveUpdateChannel?: (settings: UpdateChannelSettings) => Promise<UpdateChannelSettings>;
  onAuthorizeHostUpdate?: () => Promise<HostUpdateManualAuthorizationResponse>;
  onExecuteHostUpdate?: (authorizationId: string) => Promise<HostUpdateStatusResponse>;
  onGetHostUpdateStatus?: (releaseId: string) => Promise<HostUpdateStatusResponse>;
  onRecoverHostUpdate?: (releaseId: string, requestId?: string) => Promise<HostUpdateRecoveryResult>;
}

function text(value: string | null | undefined) {
  return value ?? UNKNOWN;
}

type ConnectionObservation = "connected" | "unknown";

type CanonicalReleaseApplicationSource = {
  releaseId: string;
  applicationVersion: string;
  sourceCommit: string;
};

type PlatformDigestName = "manifestDigest" | "platformDigest" | "indexDigest";

const platformDigestNames: readonly PlatformDigestName[] = [
  "manifestDigest",
  "platformDigest",
  "indexDigest",
];

function canonicalReleaseApplicationSource(
  service: ServiceInventory["services"][number],
): CanonicalReleaseApplicationSource | null {
  const releaseId = service.identity?.releaseId;
  const applicationVersion = service.applicationVersion;
  // The canonical identity is preferred; the top-level source commit remains
  // useful evidence when a reporter did not duplicate it in the identity.
  const sourceCommit = service.identity?.sourceCommit ?? service.sourceCommit;
  return releaseId != null && applicationVersion != null && sourceCommit != null
    ? { releaseId, applicationVersion, sourceCommit }
    : null;
}

function verificationQuality(
  service: ServiceInventory["services"][number],
  snapshotOrigin: ServiceInventory["snapshotOrigin"] | undefined,
) {
  const qualities: string[] = [];
  if (snapshotOrigin === "Imported") qualities.push("Imported");
  if (service.observationState === "Stale") qualities.push("Stale");
  if (service.verificationSource !== null && service.verifiedAt !== null) {
    qualities.push("Verified");
  } else if (service.source === "SelfReport") {
    qualities.push("Self-reported");
  }
  return qualities.join("; ") || UNKNOWN;
}

function observedIdentityDetails(inventory: ServiceInventory | null | undefined) {
  const services = inventory?.services ?? [];
  const canonicalEvidence = new Set<string>();
  const platformDigests = new Map<string, Set<string>>();
  let missingCanonicalIdentity = false;
  let missingPlatformDigestEvidence = false;

  for (const service of services) {
    // Coordinated services must agree on the canonical release, application,
    // and source evidence even when they run on different platforms.
    const canonical = canonicalReleaseApplicationSource(service);
    if (canonical) {
      canonicalEvidence.add(JSON.stringify(canonical));
    } else {
      missingCanonicalIdentity = true;
    }

    // A known digest is useful on its own. Compare each kind separately among
    // replicas of the same service on the same platform; never require the
    // other two digest fields before reporting a known divergence.
    if (service.platform == null) {
      missingPlatformDigestEvidence = true;
      continue;
    }
    let hasPlatformDigest = false;
    for (const digestName of platformDigestNames) {
      const digest = service[digestName];
      if (digest == null) continue;
      hasPlatformDigest = true;
      const key = `${service.serviceId}\u0000${service.platform}\u0000${digestName}`;
      const values = platformDigests.get(key) ?? new Set<string>();
      values.add(digest);
      platformDigests.set(key, values);
    }
    if (!hasPlatformDigest) missingPlatformDigestEvidence = true;
  }

  const hasCanonicalConflict = canonicalEvidence.size > 1;
  const hasPlatformDigestConflict = [...platformDigests.values()].some(
    (digests) => digests.size > 1,
  );
  const hasConflict = hasCanonicalConflict || hasPlatformDigestConflict;

  return (
    <section aria-labelledby="observed-identities-heading" className="space-y-2">
      <h3 id="observed-identities-heading" className="font-medium">
        Observed replica deployment fingerprints
      </h3>
      {services.length === 0 ? (
        <p>Unknown. No replica observations were reported.</p>
      ) : (
        <>
          {hasConflict && (
            <Alert type="warning" title="Conflicting observed deployments">
              {hasCanonicalConflict
                ? "Observed coordinated services report different canonical release, application, or source evidence."
                : "Like-for-like observed replicas report different platform digest evidence."}{" "}
              This is a conflicting observed deployment state, not a proposed target.
            </Alert>
          )}
          {missingCanonicalIdentity && (
            <Alert type="info" title="Missing canonical identity evidence">
              Some replicas do not report the canonical release, application, and source evidence needed for comparison.
            </Alert>
          )}
          {missingPlatformDigestEvidence && (
            <Alert type="info" title="Missing platform digest evidence">
              Some replicas do not report a platform and at least one platform digest; missing digest evidence is not reported as a conflict.
            </Alert>
          )}
          <div className="space-y-2">
            {services.map((service, index) => {
              const identity = service.identity;
              const label = `${service.component}${service.instanceId ? ` (${service.instanceId})` : ""}`;
              return (
                <details key={`${service.serviceId}-${service.instanceId ?? index}`}>
                  <summary aria-label={`${label}: ${text(identity?.releaseId)}`} className="cursor-pointer rounded focus-visible:outline-2 focus-visible:outline-offset-2">
                    {label}: {text(identity?.releaseId)}
                  </summary>
                  <dl className="mt-2 space-y-1">
                    <div><dt>Observation state</dt><dd>{service.observationState}</dd></div>
                    <div><dt>Observed at</dt><dd>{text(service.observedAt)}</dd></div>
                    <div><dt>Observation source</dt><dd>{service.source}</dd></div>
                    <div><dt>Verification quality</dt><dd>{verificationQuality(service, inventory?.snapshotOrigin)}</dd></div>
                    {service.verificationSource != null && <div><dt>Verification source</dt><dd>{service.verificationSource}</dd></div>}
                    {service.verifiedAt != null && <div><dt>Verified at</dt><dd>{service.verifiedAt}</dd></div>}
                    <div><dt>Release ID</dt><dd>{text(identity?.releaseId)}</dd></div>
                    <div><dt>Canonical version</dt><dd>{text(identity?.canonicalVersion)}</dd></div>
                    <div><dt>Base version</dt><dd>{text(identity?.baseVersion)}</dd></div>
                    <div><dt>Release channel</dt><dd>{text(identity?.channel)}</dd></div>
                    <div><dt>Source tag</dt><dd>{text(identity?.sourceTag)}</dd></div>
                    <div><dt>Source branch</dt><dd>{text(identity?.sourceBranch)}</dd></div>
                    <div><dt>Source commit</dt><dd className="break-all">{text(identity?.sourceCommit)}</dd></div>
                    <div><dt>Authorized branch head</dt><dd className="break-all">{text(identity?.authorizedBranchHead)}</dd></div>
                    <div><dt>Build ID</dt><dd>{text(identity?.buildId)}</dd></div>
                    <div><dt>Build attempt</dt><dd>{text(identity?.buildAttempt)}</dd></div>
                    <div><dt>Workflow identity</dt><dd>{text(identity?.workflowIdentity)}</dd></div>
                    <div><dt>Allocation identity</dt><dd>{text(identity?.allocationIdentity)}</dd></div>
                    <div><dt>Promotion origin release ID</dt><dd>{text(identity?.promotionOrigin?.releaseId)}</dd></div>
                    <div><dt>Promotion origin version</dt><dd>{text(identity?.promotionOrigin?.canonicalVersion)}</dd></div>
                    <div><dt>Promotion origin source commit</dt><dd className="break-all">{text(identity?.promotionOrigin?.sourceCommit)}</dd></div>
                    <div><dt>Promotion origin manifest digest</dt><dd className="break-all">{text(identity?.promotionOrigin?.manifestDigest)}</dd></div>
                    <div><dt>Promotion origin evidence</dt><dd>{text(identity?.promotionOrigin?.evidence)}</dd></div>
                    <div><dt>Platform</dt><dd>{text(service.platform)}</dd></div>
                    <div><dt>Platform digest</dt><dd className="break-all">{text(service.platformDigest)}</dd></div>
                    <div><dt>Index digest</dt><dd className="break-all">{text(service.indexDigest)}</dd></div>
                    <div><dt>Manifest digest</dt><dd className="break-all">{text(service.manifestDigest)}</dd></div>
                  </dl>
                </details>
              );
            })}
          </div>
          <h3 className="font-medium">Replica observations</h3>
          <ul className="space-y-1">
            {services.map((service, index) => (
              <li key={`${service.serviceId}-${service.instanceId ?? index}`}>
                {service.component}{service.instanceId ? ` (${service.instanceId})` : ""}: {service.observationState}; observed {text(service.observedAt)}; source {service.source}
              </li>
            ))}
          </ul>
        </>
      )}
    </section>
  );
}


function formatDateTime(value: string | null | undefined, fallback: string) {
  return value ?? fallback;
}

function schedulerReasons(reasons: readonly string[] | null | undefined) {
  return Array.isArray(reasons) && reasons.length > 0 ? reasons.join(", ") : "None reported";
}

function updateSchedulingDetails(
  scheduling: UpdateSchedulingStatus | null | undefined,
) {

  if (scheduling == null) {
    return (
      <Alert type="info" title="Scheduler unavailable">
        Automatic update scheduling is not wired for this host. Update Now and automatic controls remain unavailable until the scheduler and constrained executor gates are integrated.
      </Alert>
    );
  }

  return (
    <div className="space-y-3">
      <dl className="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
        <div><dt>Configured scheduling</dt><dd>{scheduling.configuredEnabled ? "Enabled" : "Disabled"}</dd></div>
        <div><dt>Effective scheduling</dt><dd>{scheduling.effectiveEnabled ? "Enabled" : "Disabled"}</dd></div>
        <div><dt>Selected channel</dt><dd>{scheduling.selectedChannel}</dd></div>
        <div><dt>Effective channel</dt><dd>{scheduling.effectiveChannel ?? UNKNOWN}</dd></div>
        <div><dt>Policy revision</dt><dd>{scheduling.policyRevision}</dd></div>
        <div><dt>Last attempt</dt><dd>{formatDateTime(scheduling.lastAttemptAt, "Never attempted")}</dd></div>
        <div><dt>Next attempt</dt><dd>{formatDateTime(scheduling.nextAttemptAt, "Not scheduled")}</dd></div>
        <div><dt>Backoff state</dt><dd>{scheduling.backoff.state}</dd></div>
        <div><dt>Consecutive failures</dt><dd>{scheduling.backoff.consecutiveFailures}</dd></div>
        <div><dt>Backoff until</dt><dd>{formatDateTime(scheduling.backoff.until, "Not waiting")}</dd></div>
        <div><dt>Kill switch</dt><dd>{scheduling.killSwitch.enabled ? "Enabled" : "Disabled"}</dd></div>
        <div><dt>Kill switch reason</dt><dd>{scheduling.killSwitch.reason ?? "None reported"}</dd></div>
        <div><dt>Executor state</dt><dd>{scheduling.executor.state}</dd></div>
        <div><dt>Executor reason</dt><dd>{scheduling.executor.reason ?? "None reported"}</dd></div>
      </dl>
      <p>Scheduler reasons: {schedulerReasons(scheduling.reasons)}.</p>
      <p>Backoff reasons: {schedulerReasons(scheduling.backoff.reasons)}.</p>
      {scheduling.executor.state === "Unavailable" && (
        <Alert type="info" title="Executor unavailable">
          The executor is unavailable{scheduling.executor.reason ? `: ${scheduling.executor.reason}` : ""}. This page does not infer installability from inventory or scheduling status.
        </Alert>
      )}
    </div>
  );
}

/** Read-only M1 installer-update surface. It intentionally has no mutation until the constrained handoff exists. */
export function InstallerUpdatesExperience({
  inventory,
  updateScheduling,
  observation,
  updateChannelSettings,
  updateChannelIsLoading = false,
  updateChannelIsError = false,
  onRetryUpdateChannel,
  onSaveUpdateChannel,
  onAuthorizeHostUpdate,
  onExecuteHostUpdate,
  onGetHostUpdateStatus,
  onRecoverHostUpdate,
}: InstallerUpdatesExperienceProps) {
  const [channel, setChannel] = useState<UpdateChannel>(updateChannelSettings?.channel ?? "stable");
  const [acknowledgementOpen, setAcknowledgementOpen] = useState(false);
  const [insiderAcknowledgementDraft, setInsiderAcknowledgementDraft] = useState(false);
  const [savingChannel, setSavingChannel] = useState(false);
  const [channelError, setChannelError] = useState<string | null>(null);
  const [channelStatus, setChannelStatus] = useState("");
  // True only while a save outcome could not be confirmed by either the POST
  // or an authoritative refetch. Mutation controls stay disabled the whole
  // time this is true. This is cleared explicitly from the resolved value of
  // an operation (save/reject/retry) below -- never inferred from whether
  // the `updateChannelSettings` prop object changed identity, since TanStack
  // Query's structural sharing can return the exact same cached reference
  // for a deep-equal refetch result and would otherwise leave this stuck.
  const [saveOutcomeUnknown, setSaveOutcomeUnknown] = useState(false);
  // True only while an explicit GET-only retry (triggered from this
  // component) is in flight, so a double-click cannot fire it twice.
  const [retryingUpdateChannel, setRetryingUpdateChannel] = useState(false);
  const [manualUpdateOpen, setManualUpdateOpen] = useState(false);
  const [manualUpdateBusy, setManualUpdateBusy] = useState(
    () => readManualUpdateReleaseId() != null && onGetHostUpdateStatus != null,
  );
  const [manualUpdateError, setManualUpdateError] = useState<string | null>(null);
  const [manualUpdateStatus, setManualUpdateStatus] = useState<HostUpdateStatusResponse | null>(null);
  const [manualUpdateRecovery, setManualUpdateRecovery] = useState<HostUpdateRecoveryResult | null>(null);
  const [manualUpdateReleaseId, setManualUpdateReleaseId] = useState<string | null>(readManualUpdateReleaseId);
  const initialManualUpdateReleaseId = useRef(manualUpdateReleaseId);
  const [manualUpdateAttempted, setManualUpdateAttempted] = useState(false);
  const manualUpdateDispatchLock = useRef(false);
  const rehydrationAttempted = useRef(false);
  // Set immediately (synchronously, before any state update) by an
  // operation outcome handler -- save success, confirmed rejection, or
  // GET-only retry -- to the exact settings it just reconciled the UI to.
  // The props synchronization effect below consumes this once to avoid
  // clobbering that outcome's error/status text when the parent's query
  // cache produces a new `updateChannelSettings` object with the same
  // content immediately afterward (e.g. the same refetch the operation
  // itself awaited). It is intentionally transient (consumed on first
  // matching render), not a persistent last-known-content cache, so an
  // unrelated later settings arrival with coincidentally equal content is
  // still processed normally.
  const pendingLocalReconciliationRef = useRef<UpdateChannelSettings | null>(null);

  useEffect(() => {
    if (!updateChannelSettings) return;
    const pending = pendingLocalReconciliationRef.current;
    const matchesPending =
      pending != null &&
      pending.channel === updateChannelSettings.channel &&
      pending.insiderAcknowledged === updateChannelSettings.insiderAcknowledged;
    pendingLocalReconciliationRef.current = null;
    if (matchesPending) {
      // This settings arrival is the same content an operation outcome
      // handler already reconciled the UI to explicitly; do not clobber
      // that outcome's error/status text.
      return;
    }
    setChannel(updateChannelSettings.channel);
    // A genuinely new authoritative settings value (initial load, an
    // external change, or a GET-only retry not already handled explicitly)
    // resolves any prior unknown outcome and clears stale error text.
    setSaveOutcomeUnknown(false);
    setChannelError(null);
  }, [updateChannelSettings]);

  useEffect(() => {
    const releaseId = initialManualUpdateReleaseId.current;
    if (!releaseId || !onGetHostUpdateStatus || rehydrationAttempted.current) return;
    rehydrationAttempted.current = true;
    let active = true;
    void onGetHostUpdateStatus(releaseId).then((status) => {
      if (!active) return;
      setManualUpdateStatus(status);
      setManualUpdateOpen(true);
      setManualUpdateBusy(false);
      if (isTerminalForReleaseIdentity(status)) {
        clearManualUpdateReleaseId();
        setManualUpdateReleaseId(null);
      }
    }).catch((error) => {
      if (!active) return;
      if (isApiError(error) && error.statusCode === 404) {
        clearManualUpdateReleaseId();
        setManualUpdateReleaseId(null);
        setManualUpdateBusy(false);
        return;
      }
      setManualUpdateError(getErrorMessage(error, "The previous host update status could not be loaded."));
      setManualUpdateOpen(true);
      setManualUpdateBusy(false);
    });
    return () => { active = false; };
  }, [onGetHostUpdateStatus]);

  const settingsLoaded = updateChannelSettings != null && !updateChannelIsLoading && !updateChannelIsError;
  const channelControlDisabled = savingChannel || !settingsLoaded || !onSaveUpdateChannel || saveOutcomeUnknown || retryingUpdateChannel;
  const persistedInsiderAcknowledged = updateChannelSettings?.insiderAcknowledged ?? false;
  const fieldError = channelError ?? (updateChannelIsError ? "Failed to load the authoritative UpdateChannel settings. Retry before changing the release channel." : null);
  const channelDescribedBy = fieldError ? "update-channel-help update-channel-error" : "update-channel-help";

  const closeAcknowledgementDialog = () => {
    if (savingChannel) return;
    setAcknowledgementOpen(false);
    setInsiderAcknowledgementDraft(false);
  };

  const retryUpdateChannel = async () => {
    if (!onRetryUpdateChannel || retryingUpdateChannel) return;
    setRetryingUpdateChannel(true);
    try {
      // An explicit successful GET is authoritative on its own: reconcile
      // and unlock from this resolved value directly, regardless of
      // whether the parent's query cache later hands this component the
      // same object reference (structural sharing) or a new one.
      const authoritative = await onRetryUpdateChannel();
      pendingLocalReconciliationRef.current = authoritative;
      setChannel(authoritative.channel);
      setSaveOutcomeUnknown(false);
      setChannelError(null);
      setChannelStatus("");
    } catch {
      // The retry itself could not confirm anything; leave the existing
      // unknown-outcome/error state as-is so the admin can retry again.
    } finally {
      setRetryingUpdateChannel(false);
    }
  };

  const saveChannel = async (
    settings: UpdateChannelSettings,
    options: { closeAcknowledgementOnSuccess?: boolean } = {},
  ) => {
    if (!onSaveUpdateChannel) return;
    setSavingChannel(true);
    setChannelError(null);
    setChannelStatus("");
    try {
      // A resolved promise means an authoritative refetch confirmed the
      // requested settings were actually applied; only then is success
      // reported, regardless of whether the POST itself resolved or
      // rejected.
      const authoritativeSettings = await onSaveUpdateChannel(settings);
      pendingLocalReconciliationRef.current = authoritativeSettings;
      setChannel(authoritativeSettings.channel);
      setChannelStatus("Update channel saved.");
      setInsiderAcknowledgementDraft(false);
      setSaveOutcomeUnknown(false);
      if (options.closeAcknowledgementOnSuccess) {
        setAcknowledgementOpen(false);
      }
    } catch (error) {
      if (error instanceof UpdateChannelSaveRejectedError) {
        // The authoritative refetch succeeded and disagrees with the
        // request: this is a confirmed rejection/unchanged state, not an
        // unknown one. Reconcile the UI to the real server value instead of
        // claiming success, and leave mutation controls enabled since the
        // state is known.
        pendingLocalReconciliationRef.current = error.authoritative;
        setChannel(error.authoritative.channel);
        const acknowledgementMismatch =
          error.authoritative.channel === settings.channel &&
          error.authoritative.insiderAcknowledged !== settings.insiderAcknowledged;
        setChannelError(
          acknowledgementMismatch
            ? "Update channel was not saved. The server did not record the Insider acknowledgement."
            : `Update channel was not saved. The server still reports "${error.authoritative.channel}".`,
        );
        setSaveOutcomeUnknown(false);
        // The outcome is conclusively known (not pending): close the
        // acknowledgement dialog rather than leaving it open over a control
        // that has already reverted to the authoritative value.
        setAcknowledgementOpen(false);
        setInsiderAcknowledgementDraft(false);
      } else {
        // Neither the POST nor the refetch could confirm the outcome. Keep
        // mutation controls disabled until a fresh authoritative GET
        // resolves this.
        setSaveOutcomeUnknown(true);
        setChannelError("Update channel save outcome is unknown because the authoritative UpdateChannel settings could not be confirmed. Retry before saving again.");
      }
    } finally {
      setSavingChannel(false);
    }
  };

  const selectedTrain = updateChannelSettings?.channel ?? inventory?.selectedChannel ?? UNKNOWN;
  const insider = updateChannelSettings?.channel === "insider" || inventory?.selectedChannel === "insider" || inventory?.observedChannel === "insider";
  const readiness = inventory?.readiness;
  const blocked =
    readiness?.state === "Blocked" ||
    inventory?.eligibility === "Blocked" ||
    inventory?.compatibilityState === "Incompatible" ||
    inventory?.compatibilityState === "MixedChannel" ||
    inventory?.compatibilityState === "MixedRelease";
  const eligibilityReasons = Array.isArray(inventory?.eligibilityReasons)
    ? inventory.eligibilityReasons
    : [];
  const readinessReasons = Array.isArray(readiness?.reasons)
    ? readiness.reasons
    : [];
  const readinessHops = Array.isArray(readiness?.hops) ? readiness.hops : [];
  const manualUpdateAvailable =
    observation === "connected" &&
    readiness?.state === "Eligible" &&
    inventory?.eligibility === "Eligible" &&
    !blocked &&
    onAuthorizeHostUpdate != null &&
    onExecuteHostUpdate != null;

  const authorizeAndExecuteHostUpdate = async () => {
    if (
      !onAuthorizeHostUpdate ||
      !onExecuteHostUpdate ||
      manualUpdateBusy ||
      manualUpdateAttempted ||
      manualUpdateDispatchLock.current
    ) return;
    manualUpdateDispatchLock.current = true;
    setManualUpdateBusy(true);
    setManualUpdateError(null);
    setManualUpdateRecovery(null);
    let releaseId: string | null = null;
    let executeDispatched = false;
    try {
      const authorization = await onAuthorizeHostUpdate();
      releaseId = authorization.releaseId;
      if (manualUpdateReleaseId && manualUpdateReleaseId !== releaseId) {
        setManualUpdateStatus(null);
        setManualUpdateRecovery(null);
        clearManualUpdateReleaseId();
      }
      setManualUpdateReleaseId(releaseId);
      writeManualUpdateReleaseId(releaseId);
      setManualUpdateAttempted(true);
      executeDispatched = true;
      const status = await onExecuteHostUpdate(authorization.authorizationId);
      setManualUpdateStatus(status);
      if (isTerminalForRetry(status)) {
        if (isTerminalForReleaseIdentity(status)) {
          clearManualUpdateReleaseId();
          setManualUpdateReleaseId(null);
        }
        setManualUpdateAttempted(false);
        manualUpdateDispatchLock.current = false;
      }
      setManualUpdateOpen(true);
    } catch (error) {
      if (executeDispatched && isApiError(error) && error.statusCode === 503) {
        clearManualUpdateReleaseId();
        setManualUpdateReleaseId(null);
        setManualUpdateAttempted(false);
        manualUpdateDispatchLock.current = false;
        setManualUpdateError("The host update subsystem is unavailable on this host. No update was started.");
        setManualUpdateOpen(true);
        return;
      }
      if (executeDispatched && releaseId && onGetHostUpdateStatus) {
        try {
          const status = await onGetHostUpdateStatus(releaseId);
          setManualUpdateStatus(status);
          setManualUpdateOpen(true);
          const definitiveRejection = isApiError(error) && [400, 409, 422].includes(error.statusCode);
          if (definitiveRejection) {
            clearManualUpdateReleaseId();
            setManualUpdateReleaseId(null);
            setManualUpdateStatus(null);
            setManualUpdateAttempted(false);
            manualUpdateDispatchLock.current = false;
            setManualUpdateError(getErrorMessage(error, "The host update was rejected before it could start."));
            return;
          }
          if (isTerminalForRetry(status)) {
            if (isTerminalForReleaseIdentity(status)) {
              clearManualUpdateReleaseId();
              setManualUpdateReleaseId(null);
            }
            setManualUpdateAttempted(false);
            manualUpdateDispatchLock.current = false;
          }
          return;
        } catch {
          // Preserve the original execute error when status cannot yet be read.
        }
      }
      if (!executeDispatched || (isApiError(error) && [400, 409, 422].includes(error.statusCode))) {
        clearManualUpdateReleaseId();
        setManualUpdateReleaseId(null);
        setManualUpdateAttempted(false);
        manualUpdateDispatchLock.current = false;
      }
      setManualUpdateError(isApiError(error) && error.statusCode === 503
        ? "The host update subsystem is unavailable on this host. No update was started."
        : getErrorMessage(error, "The host update could not be authorized or started."));
      setManualUpdateOpen(true);
    } finally {
      if (!executeDispatched) {
        manualUpdateDispatchLock.current = false;
      }
      setManualUpdateBusy(false);
    }
  };

  const refreshManualUpdateStatus = async () => {
    if (!manualUpdateStatus || !onGetHostUpdateStatus || manualUpdateBusy) return;
    setManualUpdateBusy(true);
    setManualUpdateError(null);
    try {
      const status = await onGetHostUpdateStatus(manualUpdateStatus.releaseId);
      setManualUpdateStatus(status);
      if (isTerminalForRetry(status)) {
        if (isTerminalForReleaseIdentity(status)) {
          clearManualUpdateReleaseId();
          setManualUpdateReleaseId(null);
          setManualUpdateStatus(null);
        }
        setManualUpdateAttempted(false);
        manualUpdateDispatchLock.current = false;
      }
    } catch (error) {
      setManualUpdateError(getErrorMessage(error, "Update status could not be loaded."));
    } finally {
      setManualUpdateBusy(false);
    }
  };

  const recoverManualUpdate = async () => {
    if (!manualUpdateStatus || !onRecoverHostUpdate || manualUpdateBusy) return;
    setManualUpdateBusy(true);
    setManualUpdateError(null);
    try {
      const recovery = await onRecoverHostUpdate(manualUpdateStatus.releaseId);
      setManualUpdateRecovery(recovery);
      if (onGetHostUpdateStatus) {
        const status = await onGetHostUpdateStatus(manualUpdateStatus.releaseId);
        setManualUpdateStatus(status);
        if (isTerminalForRetry(status)) {
          if (isTerminalForReleaseIdentity(status)) {
            clearManualUpdateReleaseId();
            setManualUpdateReleaseId(null);
            setManualUpdateStatus(null);
          }
          setManualUpdateAttempted(false);
          manualUpdateDispatchLock.current = false;
        }
      } else if (recovery.outcome === "RolledBack") {
        clearManualUpdateReleaseId();
        setManualUpdateReleaseId(null);
        setManualUpdateStatus(null);
        setManualUpdateRecovery(null);
        setManualUpdateAttempted(false);
        manualUpdateDispatchLock.current = false;
      } else if (recovery.outcome === "NeedsOperator") {
        setManualUpdateAttempted(false);
        manualUpdateDispatchLock.current = false;
      }
    } catch (error) {
      setManualUpdateError(getErrorMessage(error, "Recovery could not be completed."));
    } finally {
      setManualUpdateBusy(false);
    }
  };

  return (
    <div className="space-y-4" data-testid="installer-updates">
      {observation === "unknown" && (
        <div
          role="status"
          aria-live="polite"
          aria-label="Connection observation unknown"
        >
          <Alert type="warning" title="Connection observation unknown">
            The browser is disconnected. An update outcome is not inferred; the
            page will reconcile its snapshot after reconnect.
          </Alert>
        </div>
      )}
      <Alert
        type={blocked ? "error" : "info"}
        title="Read-only release availability"
      >
        {blocked
          ? "The observed installation is blocked. Wait, fix forward, or use the documented restore path; downgrade is not offered as a bypass."
          : "Availability is read-only until trusted host evidence and the constrained executor are accepted. Missing evidence is not treated as installable."}
      </Alert>
      {insider && (
        <Alert type="warning" title="Insider channel">
          {INSIDER_WARNING}
        </Alert>
      )}
      <Card>
        <Card.Header>
          <h2 className="text-lg font-semibold">
            Release trains and observed identity
          </h2>
        </Card.Header>
        <Card.Body className="space-y-3">
          <dl className="grid gap-2 sm:grid-cols-3">
            <div>
              <dt>Selected train</dt>
              <dd>{selectedTrain}</dd>
            </div>
            <div>
              <dt>Observed train</dt>
              <dd>
                {inventory?.observedChannel ?? UNKNOWN} —{" "}
                {inventory?.channelState ?? UNKNOWN}
              </dd>
            </div>
            <div>
              <dt>Proposed target train</dt>
              <dd>{UNKNOWN} - target-release contract unavailable</dd>
            </div>
            <div>
              <dt>Observed compatibility state</dt>
              <dd>{inventory?.compatibilityState ?? UNKNOWN}</dd>
            </div>
            <div>
              <dt>Observed compatibility reasons</dt>
              <dd>{Array.isArray(inventory?.compatibilityReasons) && inventory.compatibilityReasons.length > 0 ? inventory.compatibilityReasons.join(", ") : UNKNOWN}</dd>
            </div>
          </dl>
          {(inventory?.compatibilityState === "MixedRelease" || inventory?.compatibilityState === "Incompatible") && (
            <Alert type="error" title="Observed compatibility conflict">
              Observed compatibility is {inventory.compatibilityState}: {Array.isArray(inventory.compatibilityReasons) && inventory.compatibilityReasons.length > 0 ? inventory.compatibilityReasons.join(", ") : UNKNOWN}. This conflict is reported from the observed inventory, not as a proposed target.
            </Alert>
          )}
          {observedIdentityDetails(inventory)}
          <p>
            Proposed target release identity: {UNKNOWN}. Service inventory only
            reports observed/installed identities; a distinct verified
            target-release contract is required before target provenance can be
            shown.
          </p>
          <p>
            Snapshot provenance: {inventory?.snapshotOrigin ?? UNKNOWN}.
            Collected: {text(inventory?.collectedAt)}. Source:{" "}
            {text(inventory?.snapshotSource)}. Exported:{" "}
            {text(inventory?.snapshotExportedAt)}.
          </p>
          <p>
            Configured aliases are hints, not installed release identity.
            Digest, plan, policy, channel, or provenance drift invalidates a
            future approval rather than silently changing it.
          </p>
        </Card.Body>
      </Card>
      <Card>
        <Card.Header>
          <h2 className="text-lg font-semibold">Release notes and readiness</h2>
        </Card.Header>
        <Card.Body className="space-y-2">
          <p>
            Release notes and operation history are unavailable pending the
            read-only release contract. Features, fixes, breaking changes,
            compatibility, migrations, downtime, recovery guidance, and durable
            records are not inferred from service inventory.
          </p>
          <p>
            Readiness: {readiness?.state ?? UNKNOWN}. Eligibility:{" "}
            {inventory?.eligibility ?? UNKNOWN}.{" "}
            {eligibilityReasons.join(", ") || "No reasons reported."}
          </p>
          <p>
            Readiness reasons: {readinessReasons.join(", ") || UNKNOWN}.
            Readiness hops: {readinessHops.join(" → ") || UNKNOWN}.
          </p>
          <p>
            Later defers only this channel’s reminder; it never cancels an
            in-flight operation or changes automatic-update policy.
          </p>
          <div className="flex flex-wrap gap-2">
            <Button
              type="button"
              variant="primary"
              disabled={!manualUpdateAvailable || manualUpdateBusy}
              explainedDisabled={!manualUpdateAvailable || manualUpdateBusy}
              title={!manualUpdateAvailable
                ? MANUAL_DISABLED_REASON
                : manualUpdateBusy
                  ? "An update operation is already in progress."
                  : undefined}
              aria-describedby="manual-update-reason"
              loading={manualUpdateBusy}
              onClick={() => {
                setManualUpdateError(null);
                setManualUpdateRecovery(null);
                if (manualUpdateStatus && isTerminalForReleaseIdentity(manualUpdateStatus)) {
                  clearManualUpdateReleaseId();
                  setManualUpdateReleaseId(null);
                  setManualUpdateStatus(null);
                  setManualUpdateAttempted(false);
                  manualUpdateDispatchLock.current = false;
                }
                setManualUpdateOpen(true);
              }}
            >
              Update now
            </Button>
            <Button
              type="button"
              variant="secondary"
              disabled
              explainedDisabled
              title="No release reminder is currently available; Later cannot alter policy or an active operation."
              aria-describedby="later-update-reason"
            >
              Later
            </Button>
          </div>
          <p
            id="manual-update-reason"
            className="text-sm text-pf-text-secondary"
          >
            {manualUpdateAvailable
              ? "A verified candidate is ready. Update now will request one-time authorization before execution."
              : MANUAL_DISABLED_REASON}
          </p>
          <p
            id="later-update-reason"
            className="text-sm text-pf-text-secondary"
          >
            Later is a reminder-only action and has no effect while the required
            availability contract is absent.
          </p>
        </Card.Body>
      </Card>
      <Card>
        <Card.Header>
          <h2 className="text-lg font-semibold">Automatic updates</h2>
        </Card.Header>
        <Card.Body className="space-y-2">
          <p>
            Auto-update is off by default. Disabling it stops new work; an
            active operation stops only at safe checkpoints.
          </p>
          <fieldset aria-describedby="auto-update-reason">
            <legend className="font-medium">
              Administrator standing permission
            </legend>
            <Button
              className="mt-2 justify-start"
              role="checkbox"
              aria-checked={false}
              aria-describedby="auto-update-reason"
              disabled
              explainedDisabled
              title={AUTO_DISABLED_REASON}
              type="button"
              variant="unstyled"
            >
              Enable Auto-update for the selected train (off)
            </Button>
            <label className="mt-2 block">
              Maintenance window{" "}
              <Input
                aria-describedby="auto-update-reason"
                aria-disabled="true"
                title={AUTO_DISABLED_REASON}
                className="ml-2"
                type="text"
                value="Not configured"
                readOnly
              />
            </label>
          </fieldset>
          <Button
            type="button"
            variant="secondary"
            disabled
            explainedDisabled
            title={AUTO_DISABLED_REASON}
            aria-describedby="auto-update-reason"
          >
            Save automatic update policy
          </Button>
          <p id="auto-update-reason" className="text-sm text-pf-text-secondary">
            {AUTO_DISABLED_REASON}
          </p>
        </Card.Body>
      </Card>
      <Card>
        <Card.Header>
          <h2 className="text-lg font-semibold">Scheduler status</h2>
        </Card.Header>
        <Card.Body className="space-y-3">
          <p>
            Scheduling status is read-only. It reports configured versus effective policy and does not make Update Now or automatic installation available.
          </p>
          {updateSchedulingDetails(updateScheduling ?? inventory?.updateScheduling)}
        </Card.Body>
      </Card>
      <Card>
        <Card.Header>
          <h2 className="text-lg font-semibold">Update channel</h2>
        </Card.Header>
        <Card.Body className="space-y-3">
          <p id="update-channel-help">Choose which verified release train may be discovered. Choosing Insider does not enable installation; safe apply and recovery controls remain unavailable.</p>
          {updateChannelIsLoading && !updateChannelSettings && <p role="status">Loading update channel settings...</p>}
          {updateChannelIsError && (
            <Alert type="warning" title="Update channel settings unavailable">
              The authoritative UpdateChannel settings could not be loaded. The selector shows the stable default until settings load successfully.
              {onRetryUpdateChannel && (
                <Button className="mt-2" type="button" variant="secondary" disabled={retryingUpdateChannel} loading={retryingUpdateChannel} onClick={() => { void retryUpdateChannel(); }}>
                  Retry UpdateChannel settings
                </Button>
              )}
            </Alert>
          )}
          {saveOutcomeUnknown && !updateChannelIsError && (
            <Alert type="warning" title="Update channel save outcome unknown">
              A fresh authoritative confirmation is required before the update channel can be changed again.
              {onRetryUpdateChannel && (
                <Button className="mt-2" type="button" variant="secondary" disabled={retryingUpdateChannel} loading={retryingUpdateChannel} onClick={() => { void retryUpdateChannel(); }}>
                  Retry UpdateChannel settings
                </Button>
              )}
            </Alert>
          )}
          <FormField
            label="Release channel"
            htmlFor="update-channel"
            helper="Stable is the default. Insider may contain prerelease changes."
            error={fieldError}
            errorId="update-channel-error"
          >
            <Select
              id="update-channel"
              value={channel}
              aria-describedby={channelDescribedBy}
              aria-invalid={fieldError ? true : undefined}
              invalid={fieldError != null}
              disabled={channelControlDisabled}
              onChange={(event) => {
                setChannel(event.target.value as UpdateChannel);
                setChannelError(null);
                setChannelStatus("");
              }}
            >
              <option value="stable">Stable</option>
              <option value="insider">Insider</option>
            </Select>
          </FormField>
          {channel === "insider" && (
            <Alert type="warning" title="Pending Insider channel selection">{INSIDER_WARNING} Installation controls remain unavailable until safe apply and recovery are implemented.</Alert>
          )}
          <p role="status" aria-live="polite" aria-label="Update channel save status" className="min-h-5">{channelStatus}</p>
          <Button
            type="button"
            variant="secondary"
            loading={savingChannel && !acknowledgementOpen}
            disabled={channelControlDisabled}
            onClick={() => {
              setChannelError(null);
              setChannelStatus("");
              if (channel === "insider" && !persistedInsiderAcknowledged) {
                setInsiderAcknowledgementDraft(false);
                setAcknowledgementOpen(true);
                return;
              }
              void saveChannel({ channel, insiderAcknowledged: channel === "insider" ? persistedInsiderAcknowledged : false });
            }}
          >
            Save update channel
          </Button>
        </Card.Body>
      </Card>
      <Modal
        isOpen={acknowledgementOpen}
        onClose={closeAcknowledgementDialog}
        title="Acknowledge Insider channel risk"
        isDisabled={savingChannel}
        footer={<div className="flex justify-end gap-2"><Button type="button" variant="secondary" disabled={savingChannel} onClick={closeAcknowledgementDialog}>Cancel</Button><Button type="button" disabled={!insiderAcknowledgementDraft || savingChannel || saveOutcomeUnknown} loading={savingChannel} onClick={() => { void saveChannel({ channel: "insider", insiderAcknowledged: true }, { closeAcknowledgementOnSuccess: true }); }}>Acknowledge and save</Button></div>}
      >
        <div className="space-y-3">
          <p>{INSIDER_WARNING} Insider is intended for administrators who accept prerelease behavior and possible regressions. It only changes discovery eligibility; it does not install releases.</p>
          {channelError && <Alert type="error" title="Update channel not confirmed">{channelError}</Alert>}
          <Checkbox
            id="insider-acknowledgement"
            checked={insiderAcknowledgementDraft}
            disabled={savingChannel || saveOutcomeUnknown}
            onChange={(event) => setInsiderAcknowledgementDraft(event.target.checked)}
            label="I understand and accept the prerelease risk of Insider updates."
          />
        </div>
      </Modal>
      <Modal
        isOpen={manualUpdateOpen}
        onClose={() => {
          if (!manualUpdateBusy) setManualUpdateOpen(false);
        }}
        title={manualUpdateStatus ? "Host update progress" : "Confirm host update"}
        isDisabled={manualUpdateBusy}
        footer={
          <div className="flex justify-end gap-2">
            <Button
              type="button"
              variant="secondary"
              disabled={manualUpdateBusy}
              onClick={() => setManualUpdateOpen(false)}
            >
              Close
            </Button>
            {!manualUpdateStatus && (
              <Button
                type="button"
                variant="primary"
                loading={manualUpdateBusy}
                disabled={manualUpdateBusy || !manualUpdateAvailable || manualUpdateAttempted}
                explainedDisabled={manualUpdateBusy || !manualUpdateAvailable || manualUpdateAttempted}
                title={!manualUpdateAvailable
                  ? MANUAL_DISABLED_REASON
                  : manualUpdateBusy
                    ? "An update operation is already in progress."
                    : manualUpdateAttempted
                      ? "The previous attempt's outcome is unknown — refresh status or reload before retrying."
                      : undefined}
                onClick={() => { void authorizeAndExecuteHostUpdate(); }}
              >
                Authorize and update
              </Button>
            )}
            {manualUpdateStatus?.currentState === "RecoveryRequired" && onRecoverHostUpdate && (
              <Button
                type="button"
                variant="danger"
                loading={manualUpdateBusy}
                disabled={manualUpdateBusy}
                onClick={() => { void recoverManualUpdate(); }}
              >
                Recover update
              </Button>
            )}
          </div>
        }
      >
        {!manualUpdateStatus && !manualUpdateError && (
          <div className="space-y-3">
            <p>
              This will authorize and execute the currently verified release
              once. The host will perform its safety checks, drain, backup,
              apply, and verification steps before reporting completion.
            </p>
            <Alert type="warning" title="Host interruption expected">
              Do not close the host or interrupt its power while the operation
              is in progress. Automatic updates remain disabled.
            </Alert>
          </div>
        )}
        {manualUpdateError && (
          <Alert type="error" title="Host update unavailable">
            {manualUpdateError}
          </Alert>
        )}
        {manualUpdateStatus && (
          <div className="space-y-3">
            <p role="status" aria-live="polite">
              Current state: <strong>{manualUpdateStatus.currentState}</strong>
              {" "}({manualUpdateStatus.releaseId})
            </p>
            <ol className="space-y-2" aria-label="Host update progress">
              {manualUpdateStatus.activities.map((activity) => (
                <li key={activity.activityId} className="flex justify-between gap-3">
                  <span>{activity.phase}</span>
                  <span>{activity.state} · {formatDateTime(activity.recordedAt, UNKNOWN)}</span>
                </li>
              ))}
            </ol>
            {manualUpdateStatus.currentState === "Completed" && !isRolledBackStatus(manualUpdateStatus) && (
              <Alert type="success" title="Host update completed">
                The host reported a completed update. Refresh the installation
                observation to reconcile the running services.
              </Alert>
            )}
            {isRolledBackStatus(manualUpdateStatus) && (
              <Alert type="warning" title="Host update rolled back">
                The host rolled back the update. No new installation is active.
              </Alert>
            )}
            {onGetHostUpdateStatus && manualUpdateStatus.currentState !== "Completed" && (
              <Button
                type="button"
                variant="secondary"
                loading={manualUpdateBusy}
                disabled={manualUpdateBusy}
                onClick={() => { void refreshManualUpdateStatus(); }}
              >
                Refresh update status
              </Button>
            )}
          </div>
        )}
        {manualUpdateRecovery && (
          <Alert
            type={manualUpdateRecovery.outcome === "RolledBack" ? "success" : "warning"}
            title="Recovery result"
          >
            {manualUpdateRecovery.outcome}: {manualUpdateRecovery.detail}
          </Alert>
        )}
      </Modal>
      <Card>
        <Card.Header>
          <h2 className="text-lg font-semibold">
            Durable progress and history
          </h2>
        </Card.Header>
        <Card.Body>
          <p>
            Manual operation history appears after an authorized update starts.
            Automatic updates remain unavailable until their standing-policy
            contract is enabled for this host.
          </p>
        </Card.Body>
      </Card>
    </div>
  );
}
