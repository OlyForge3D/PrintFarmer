import type { CanonicalReleaseIdentity, ServiceInventory, ServiceReplicaObservation } from '@/types/api';

export const commit = 'a'.repeat(40);
export const digest = `sha256:${'b'.repeat(64)}`;
export const identity: CanonicalReleaseIdentity = {
  canonicalVersion: '1.2.3-insider.10', baseVersion: '1.2.3', channel: 'insider', releaseId: 'insider:1.2.3-insider.10',
  sourceTag: 'v1.2.3-insider.10', sourceBranch: 'development', sourceCommit: commit, authorizedBranchHead: commit,
  buildId: '42', buildAttempt: '1', workflowIdentity: 'release-workflow', allocationIdentity: 'allocation-10', promotionOrigin: null,
};
export function replica(overrides: Partial<ServiceReplicaObservation> = {}): ServiceReplicaObservation {
  return {
    serviceId: 'api', instanceId: 'replica-a', component: 'api', required: true, applicationVersion: '1.2.3',
    sourceCommit: commit, engineVersion: null, databaseProvider: null, migrationHead: null, observationState: 'Observed', observedAt: '2026-09-12T12:00:00Z',
    lastSuccessAt: '2026-09-12T12:00:00Z', source: 'SelfReport', reasonCode: 'AssemblyMetadataNotDigestAttestation',
    identity: null, verificationSource: null, verifiedAt: null, platform: null, platformDigest: null,
    indexDigest: null, manifestDigest: null, configuredImage: null, observedChannel: null, channelState: 'Unknown',
    compatibilityState: 'Unknown', compatibilityReasons: ['IncompleteReleaseOrPlatformEvidence'], ...overrides,
  };
}
export function inventory(overrides: Partial<ServiceInventory> = {}): ServiceInventory {
  return {
    selectedChannel: 'stable', selectionSource: 'Default', collectedAt: '2026-09-12T12:00:00Z',
    observedChannel: null, targetChannel: null, channelState: 'Unknown', compatibilityState: 'Unknown',
    compatibilityReasons: ['IncompleteReleaseOrPlatformEvidence'], eligibility: 'NotManaged',
    eligibilityReasons: ['ManagedEligibilityNotEstablished', 'ReadOnlyInventory'], readiness: null, updateScheduling: null, snapshotOrigin: 'Live', snapshotSource: null,
    snapshotExportedAt: null, services: [replica()], ...overrides,
  };
}


/** Divergent host evidence: management is not established, but readiness still blocks updates. */
export function blockedReadinessInventory(): ServiceInventory {
  return inventory({ eligibility: 'NotManaged', compatibilityState: 'Compatible', readiness: { state: 'Blocked', reasons: ['Host maintenance is required'], hops: [] } });
}

/** Replica evidence is observed, not a target; distinct identities must remain conflicting observations. */
export function conflictingReplicaInventory(): ServiceInventory {
  return inventory({ snapshotOrigin: 'Imported', snapshotSource: 'support-export', snapshotExportedAt: '2026-09-12T13:00:00Z', services: [replica({ instanceId: 'replica-a', identity }), replica({ instanceId: 'replica-b', observationState: 'Stale', identity: { ...identity, releaseId: 'stable:1.2.4', sourceCommit: 'c'.repeat(40) } })] });
}
