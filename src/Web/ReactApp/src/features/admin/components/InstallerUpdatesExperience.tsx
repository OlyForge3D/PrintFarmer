import { Alert, Button, Card, Checkbox, FormField, Input, Select } from "@/common/components/ui";
import { Modal } from "@/common/components/modals/Modal";
import type { ServiceInventory, UpdateChannel, UpdateChannelSettings } from "@/types/api";
import { useEffect, useState } from "react";

const INSIDER_WARNING =
  "Insider updates may arrive more frequently and have reduced stability compared with stable releases.";
const UNKNOWN = "Unknown";
const MANUAL_DISABLED_REASON =
  "Update now is unavailable because the runtime execution contract, including its constrained executor, fresh host evidence, recovery, reauthentication, and request-origin protections, is not available.";
const AUTO_DISABLED_REASON =
  "Auto-update is off and unavailable because the runtime scheduler, bounded standing-permission, maintenance-window, recovery, and host-policy contract is not available.";

export interface InstallerUpdatesExperienceProps {
  inventory: ServiceInventory | null | undefined;
  observation: ConnectionObservation;
  updateChannelSettings?: UpdateChannelSettings;
  updateChannelIsLoading?: boolean;
  updateChannelIsError?: boolean;
  onRetryUpdateChannel?: () => void;
  onSaveUpdateChannel?: (settings: UpdateChannelSettings) => Promise<UpdateChannelSettings>;
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


function formatDateTime(value: string | null | undefined) {
  return value ?? "Not scheduled";
}

function schedulerReasons(reasons: readonly string[] | null | undefined) {
  return Array.isArray(reasons) && reasons.length > 0 ? reasons.join(", ") : "None reported";
}

function updateSchedulingDetails(inventory: ServiceInventory | null | undefined) {
  const scheduling = inventory?.updateScheduling;

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
        <div><dt>Last attempt</dt><dd>{formatDateTime(scheduling.lastAttemptAt)}</dd></div>
        <div><dt>Next attempt</dt><dd>{formatDateTime(scheduling.nextAttemptAt)}</dd></div>
        <div><dt>Backoff state</dt><dd>{scheduling.backoff.state}</dd></div>
        <div><dt>Consecutive failures</dt><dd>{scheduling.backoff.consecutiveFailures}</dd></div>
        <div><dt>Backoff until</dt><dd>{formatDateTime(scheduling.backoff.until)}</dd></div>
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
  observation,
  updateChannelSettings,
  updateChannelIsLoading = false,
  updateChannelIsError = false,
  onRetryUpdateChannel,
  onSaveUpdateChannel,
}: InstallerUpdatesExperienceProps) {
  const [channel, setChannel] = useState<UpdateChannel>(updateChannelSettings?.channel ?? "stable");
  const [acknowledgementOpen, setAcknowledgementOpen] = useState(false);
  const [insiderAcknowledgementDraft, setInsiderAcknowledgementDraft] = useState(false);
  const [savingChannel, setSavingChannel] = useState(false);
  const [channelError, setChannelError] = useState<string | null>(null);
  const [channelStatus, setChannelStatus] = useState("");

  useEffect(() => {
    if (!updateChannelSettings) return;
    setChannel(updateChannelSettings.channel);
  }, [updateChannelSettings]);

  const settingsLoaded = updateChannelSettings != null && !updateChannelIsLoading && !updateChannelIsError;
  const channelControlDisabled = savingChannel || !settingsLoaded || !onSaveUpdateChannel;
  const persistedInsiderAcknowledged = updateChannelSettings?.insiderAcknowledged ?? false;
  const fieldError = channelError ?? (updateChannelIsError ? "Failed to load the authoritative UpdateChannel settings. Retry before changing the release channel." : null);
  const channelDescribedBy = fieldError ? "update-channel-help update-channel-error" : "update-channel-help";

  const closeAcknowledgementDialog = () => {
    if (savingChannel) return;
    setAcknowledgementOpen(false);
    setInsiderAcknowledgementDraft(false);
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
      const authoritativeSettings = await onSaveUpdateChannel(settings);
      setChannel(authoritativeSettings.channel);
      setChannelStatus("Update channel saved.");
      setInsiderAcknowledgementDraft(false);
      if (options.closeAcknowledgementOnSuccess) {
        setAcknowledgementOpen(false);
      }
    } catch {
      setChannelError("Update channel save outcome is unknown because the authoritative UpdateChannel settings could not be confirmed. Retry before saving again.");
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
              disabled
              explainedDisabled
              title={MANUAL_DISABLED_REASON}
              aria-describedby="manual-update-reason"
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
            {MANUAL_DISABLED_REASON}
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
          {updateSchedulingDetails(inventory)}
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
                <Button className="mt-2" type="button" variant="secondary" onClick={onRetryUpdateChannel}>
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
        footer={<div className="flex justify-end gap-2"><Button type="button" variant="secondary" disabled={savingChannel} onClick={closeAcknowledgementDialog}>Cancel</Button><Button type="button" disabled={!insiderAcknowledgementDraft || savingChannel} loading={savingChannel} onClick={() => { void saveChannel({ channel: "insider", insiderAcknowledged: true }, { closeAcknowledgementOnSuccess: true }); }}>Acknowledge and save</Button></div>}
      >
        <div className="space-y-3">
          <p>{INSIDER_WARNING} Insider is intended for administrators who accept prerelease behavior and possible regressions. It only changes discovery eligibility; it does not install releases.</p>
          {channelError && <Alert type="error" title="Update channel not confirmed">{channelError}</Alert>}
          <Checkbox
            id="insider-acknowledgement"
            checked={insiderAcknowledgementDraft}
            onChange={(event) => setInsiderAcknowledgementDraft(event.target.checked)}
            label="I understand and accept the prerelease risk of Insider updates."
          />
        </div>
      </Modal>
      <Card>
        <Card.Header>
          <h2 className="text-lg font-semibold">
            Durable progress and history
          </h2>
        </Card.Header>
        <Card.Body>
          <p>
            No durable update operation history is reported by this service
            inventory. Operation records and recovery results require a separate
            trusted runtime contract and are not inferred here.
          </p>
        </Card.Body>
      </Card>
    </div>
  );
}
