import { useEffect, useState } from 'react';
import { Alert, Button, Card, Checkbox, Input } from '@/common/components/ui';
import type { ServiceInventory } from '@/types/api';

const INSIDER_WARNING = 'Insider updates may arrive more frequently and have reduced stability compared with stable releases.';
const UNKNOWN = 'Unknown';
const MANUAL_DISABLED_REASON = 'Update now is unavailable: the accepted constrained executor, fresh host evidence, recovery, reauthentication, and request-origin contract are not available.';
const AUTO_DISABLED_REASON = 'Auto-update is off and unavailable: the accepted scheduler, bounded standing-permission, maintenance-window, recovery, and host-policy contract are not available.';

export interface InstallerUpdatesExperienceProps {
  inventory: ServiceInventory | null | undefined;
  canExecute: boolean;
  refetch: () => void;
}

function observedIdentityDetails(inventory: ServiceInventory | null | undefined) {
  const identity = inventory?.services.find(service => service.identity)?.identity;
  if (!identity) return <p>Observed/installed release identity: {UNKNOWN}. No service identity was observed.</p>;

  return <details><summary className="cursor-pointer rounded focus-visible:outline-2 focus-visible:outline-offset-2">Observed/installed release identity</summary><dl className="mt-2 space-y-1"><div><dt>Release ID</dt><dd>{identity.releaseId}</dd></div><div><dt>Source commit</dt><dd className="break-all">{identity.sourceCommit}</dd></div><div><dt>Authorized branch head</dt><dd className="break-all">{identity.authorizedBranchHead}</dd></div><div><dt>Promotion origin</dt><dd>{identity.promotionOrigin?.releaseId ?? UNKNOWN}</dd></div></dl></details>;
}

/** Read-only M1 installer-update surface. It intentionally has no mutation until the constrained handoff exists. */
export function InstallerUpdatesExperience({ inventory, canExecute, refetch }: InstallerUpdatesExperienceProps) {
  const [observation, setObservation] = useState(navigator.onLine ? 'connected' : 'unknown');
  useEffect(() => {
    const reconnect = () => { setObservation('connected'); refetch(); };
    const disconnect = () => setObservation('unknown');
    window.addEventListener('online', reconnect);
    window.addEventListener('offline', disconnect);
    return () => { window.removeEventListener('online', reconnect); window.removeEventListener('offline', disconnect); };
  }, [refetch]);

  const insider = inventory?.selectedChannel === 'insider' || inventory?.observedChannel === 'insider';
  const blocked = inventory?.eligibility === 'Blocked' || inventory?.compatibilityState === 'Incompatible' || inventory?.compatibilityState === 'MixedChannel';
  const executionReason = canExecute ? MANUAL_DISABLED_REASON : `Administrator execute authorization is required. ${MANUAL_DISABLED_REASON}`;
  const readiness = inventory?.readiness;
  const eligibilityReasons = inventory?.eligibilityReasons ?? [];
  const readinessReasons = readiness?.reasons ?? [];
  const readinessHops = readiness?.hops ?? [];

  return <div className="space-y-4" data-testid="installer-updates">
    {observation === 'unknown' && <div role="status" aria-live="polite" aria-label="Connection observation unknown"><Alert type="warning" title="Connection observation unknown">The browser is disconnected. An update outcome is not inferred; the page will reconcile its snapshot after reconnect.</Alert></div>}
    <Alert type={blocked ? 'error' : 'info'} title="Read-only release availability">{blocked ? 'The observed installation is blocked. Wait, fix forward, or use the documented restore path; downgrade is not offered as a bypass.' : 'Availability is read-only until trusted host evidence and the constrained executor are accepted. Missing evidence is not Ready to install.'}</Alert>
    {insider && <Alert type="warning" title="Insider channel">{INSIDER_WARNING}</Alert>}
    <Card><Card.Header><h2 className="text-lg font-semibold">Release trains and observed identity</h2></Card.Header><Card.Body className="space-y-3"><dl className="grid gap-2 sm:grid-cols-3"><div><dt>Selected train</dt><dd>{inventory?.selectedChannel ?? UNKNOWN}</dd></div><div><dt>Observed train</dt><dd>{inventory?.observedChannel ?? UNKNOWN} — {inventory?.channelState ?? UNKNOWN}</dd></div><div><dt>Proposed target train</dt><dd>{inventory?.targetChannel ?? UNKNOWN}</dd></div></dl>{observedIdentityDetails(inventory)}<p>Proposed target release identity: {UNKNOWN}. Service inventory only reports observed/installed identities; a distinct verified target-release contract is required before target provenance can be shown.</p><p>Configured aliases are hints, not installed release identity. Digest, plan, policy, channel, or provenance drift invalidates a future approval rather than silently changing it.</p></Card.Body></Card>
    <Card><Card.Header><h2 className="text-lg font-semibold">Release notes and readiness</h2></Card.Header><Card.Body className="space-y-2"><p>Release notes and operation history are unavailable pending the read-only release contract. Features, fixes, breaking changes, compatibility, migrations, downtime, recovery guidance, and durable records are not inferred from service inventory.</p><p>Readiness: {readiness?.state ?? UNKNOWN}. Eligibility: {inventory?.eligibility ?? UNKNOWN}. {eligibilityReasons.join(', ') || 'No host evidence reported.'}</p><p>Readiness reasons: {readinessReasons.join(', ') || UNKNOWN}. Readiness hops: {readinessHops.join(' → ') || UNKNOWN}.</p><p>Later defers only this channel’s reminder; it never cancels an in-flight operation or changes automatic-update policy.</p><div className="flex flex-wrap gap-2"><Button type="button" variant="primary" disabled explainedDisabled title={executionReason} aria-describedby="manual-update-reason">Update now</Button><Button type="button" variant="secondary" disabled explainedDisabled title="No release reminder is currently available; Later cannot alter policy or an active operation." aria-describedby="later-update-reason">Later</Button></div><p id="manual-update-reason" className="text-sm text-pf-text-secondary">{executionReason}</p><p id="later-update-reason" className="text-sm text-pf-text-secondary">Later is a reminder-only action and has no effect while the required availability contract is absent.</p></Card.Body></Card>
    <Card><Card.Header><h2 className="text-lg font-semibold">Automatic updates</h2></Card.Header><Card.Body className="space-y-2"><p>Auto-update is off by default. Disabling it stops new work; an active operation stops only at safe checkpoints.</p><fieldset disabled><legend className="font-medium">Administrator standing permission</legend><label className="mt-2 flex gap-2"><Checkbox /> Enable Auto-update for the selected train</label><label className="mt-2 block">Maintenance window <Input className="ml-2" type="text" value="Not configured" readOnly /></label></fieldset><Button type="button" variant="secondary" disabled explainedDisabled title={AUTO_DISABLED_REASON} aria-describedby="auto-update-reason">Save automatic update policy</Button><p id="auto-update-reason" className="text-sm text-pf-text-secondary">{AUTO_DISABLED_REASON}</p></Card.Body></Card>
    <Card><Card.Header><h2 className="text-lg font-semibold">Durable progress and history</h2></Card.Header><Card.Body><p role="status">Durable update operation records are unavailable pending the read-only contract. When the contract is available, this section will show accepted, skipped, deferred, failed, completed, recovered, and NeedsOperator records with timestamp, reason, actor, immutable prior/target/observed identity, manifest digest, policy revision, and recovery result.</p></Card.Body></Card>
  </div>;
}
