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
    sourceCommit: commit, engineVersion: null, observationState: 'Observed', observedAt: '2026-09-12T12:00:00Z',
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
    eligibilityReasons: ['ReadOnlyInventory'], services: [replica()], ...overrides,
  };
}
