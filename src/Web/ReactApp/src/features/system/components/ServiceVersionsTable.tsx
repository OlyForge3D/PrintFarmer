import { Alert, Card, Table, TableBody, TableCell, TableHead, TableHeaderCell, TableRow } from '@/common/components/ui';
import { buildInfo } from '@/common/utils/buildInfo';
import type { ServiceInventory, ServiceReplicaObservation } from '@/types/api';

const UNKNOWN = 'Unknown';
const INSIDER_WARNING = 'Insider updates may arrive more frequently and have reduced stability compared with stable releases.';

function timestamp(value: string | null): string {
  if (!value || !Number.isFinite(Date.parse(value))) return UNKNOWN;
  return new Date(value).toLocaleString();
}

function EvidenceDetails({ service }: { service: ServiceReplicaObservation }) {
  const identity = service.identity;
  const details: [string, string | null | undefined][] = [
    ['Source commit', service.sourceCommit],
    ['Canonical version', identity?.canonicalVersion],
    ['Release ID', identity?.releaseId],
    ['Source tag', identity?.sourceTag],
    ['Source branch at authorization', identity?.sourceBranch],
    ['Authorized branch head', identity?.authorizedBranchHead],
    ['Build ID', identity?.buildId],
    ['Build attempt', identity?.buildAttempt],
    ['Workflow identity', identity?.workflowIdentity],
    ['Allocation identity', identity?.allocationIdentity],
    ['Platform', service.platform],
    ['Running platform digest', service.platformDigest],
    ['Image index digest', service.indexDigest],
    ['Release manifest digest', service.manifestDigest],
    ['Configured image reference (not installed identity)', service.configuredImage],
    ['Verification source', service.verificationSource],
    ['Verified at', timestamp(service.verifiedAt)],
    ['Promotion origin release', identity?.promotionOrigin?.releaseId],
    ['Promotion origin version', identity?.promotionOrigin?.canonicalVersion],
    ['Promotion origin commit', identity?.promotionOrigin?.sourceCommit],
    ['Promotion origin manifest digest', identity?.promotionOrigin?.manifestDigest],
    ['Promotion qualification evidence', identity?.promotionOrigin?.evidence],
  ];
  return (
    <details>
      <summary className="cursor-pointer rounded focus-visible:outline-2 focus-visible:outline-offset-2">Identity and provenance details</summary>
      <dl className="mt-2 space-y-2">
        {details.map(([label, value]) => <div key={label}><dt className="font-medium">{label}</dt><dd className="break-all whitespace-normal">{value || UNKNOWN}</dd></div>)}
      </dl>
    </details>
  );
}

/** Detailed inventory belongs only in the permission-gated status page, not the pulse summary. */
export function ServiceVersionsTable({ inventory }: { inventory: ServiceInventory }) {
  const api = inventory.services.find(service => service.component === 'api');
  const assetCommit = /^[0-9a-f]{40}$/i.test(buildInfo.commit) ? buildInfo.commit : null;
  const assetSkew = (assetCommit !== null && api?.sourceCommit != null && assetCommit !== api.sourceCommit)
    || (buildInfo.releaseIdentity?.releaseId != null && api?.identity?.releaseId != null
      && buildInfo.releaseIdentity.releaseId !== api.identity.releaseId);
  const displayedCompatibility = assetSkew ? 'Incompatible' : inventory.compatibilityState;
  const displayedEligibility = assetSkew ? 'Blocked' : inventory.eligibility;
  const insider = inventory.selectedChannel === 'insider' || buildInfo.releaseIdentity?.channel === 'insider' || inventory.services.some(service => service.observedChannel === 'insider');

  return (
    <Card>
      <Card.Header><h3 className="text-lg font-semibold">Service and replica inventory</h3></Card.Header>
      <Card.Body className="space-y-4">
        <p>Read-only observations. Service self-report is not verified running image provenance. No update checks or execution are enabled here.</p>
        <dl className="grid gap-2 sm:grid-cols-2">
          <div><dt>Selected channel</dt><dd>{inventory.selectedChannel} ({inventory.selectionSource})</dd></div>
          <div><dt>Observed channel</dt><dd>{inventory.observedChannel ?? UNKNOWN} — {inventory.channelState}</dd></div>
          <div><dt>Target channel</dt><dd>{inventory.targetChannel ?? 'Unknown — no release checks'}</dd></div>
          <div><dt>Compatibility</dt><dd>{displayedCompatibility}: {assetSkew ? 'CachedFrontendMismatch' : inventory.compatibilityReasons.join(', ')}</dd></div>
          <div><dt>Normal update eligibility</dt><dd>{displayedEligibility}: {assetSkew ? 'CachedFrontendMismatch, ReadOnlyInventory' : inventory.eligibilityReasons.join(', ')}</dd></div>
          <div><dt>Inventory collected</dt><dd>{timestamp(inventory.collectedAt)}</dd></div>
        </dl>
        {insider && <Alert type="warning" title="Insider channel">{INSIDER_WARNING}</Alert>}
        {inventory.compatibilityState === 'MixedChannel' && <Alert type="error" title="Mixed channels — blocked / unsafe">Stable and insider applications are not a healthy compatible update set.</Alert>}
        <section aria-label="Loaded frontend assets" className="space-y-2">
          <h4 className="font-semibold">Loaded frontend assets</h4>
          <p className="break-all">Source commit: {assetCommit ?? UNKNOWN}. Build time: {timestamp(buildInfo.buildTime)}.</p>
          <p>Frontend canonical version: {buildInfo.releaseIdentity?.canonicalVersion ?? UNKNOWN}. Reported release: {buildInfo.releaseIdentity?.releaseId ?? UNKNOWN}. Reported channel: {buildInfo.releaseIdentity?.channel ?? UNKNOWN}.</p>
          <p>Frontend provenance: self-report, not verified running image identity. This is the loaded bundle, not the API build.</p>
          {assetSkew && <Alert type="warning" title="Frontend/API build mismatch — refresh required">Frontend/API compatibility: Incompatible under the same-build policy. Cached assets differ from this API. Refresh the page to load current assets; if the mismatch remains, reconcile the deployment. Compatibility is not established by refresh alone.</Alert>}
        </section>
        <Table>
          <TableHead><TableRow>
            <TableHeaderCell scope="col">Service / replica</TableHeaderCell>
            <TableHeaderCell scope="col">Application build / engine</TableHeaderCell>
            <TableHeaderCell scope="col">Observation</TableHeaderCell>
            <TableHeaderCell scope="col">Channel / compatibility</TableHeaderCell>
            <TableHeaderCell scope="col">Provenance</TableHeaderCell>
          </TableRow></TableHead>
          <TableBody>{inventory.services.map(service => <TableRow key={`${service.serviceId}:${service.instanceId ?? 'unobserved'}`}>
            <TableHeaderCell scope="row"><span>{service.component}</span><br /><span className="break-all">{service.instanceId ?? 'No observed replica'}</span><br />{service.required ? 'Required' : 'Optional'}</TableHeaderCell>
            <TableCell>Application build: {service.applicationVersion ?? UNKNOWN}<br />Engine: {service.engineVersion ?? UNKNOWN}</TableCell>
            <TableCell>{service.observationState}<br />{service.reasonCode}<br />Source: {service.source}<br />Last observation: {timestamp(service.observedAt)}<br />Last success: {timestamp(service.lastSuccessAt)}</TableCell>
            <TableCell>{service.observedChannel ?? UNKNOWN} — {service.channelState}<br />{service.compatibilityState}<br />{service.compatibilityReasons.join(', ')}</TableCell>
            <TableCell><EvidenceDetails service={service} /></TableCell>
          </TableRow>)}</TableBody>
        </Table>
      </Card.Body>
    </Card>
  );
}
