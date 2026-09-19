import type { HostUpdateStatusResponse } from "@/types/api";

const recoveryRequiredStatus = {
  releaseId: "release-1",
  currentState: "RecoveryRequired",
  activities: [{
    activityId: "activity-1",
    releaseId: "release-1",
    state: "RecoveryRequired",
    phase: "apply",
    recordedAt: "2026-09-19T19:00:00Z",
    requestBinding: {
      releaseId: "release-1",
      authenticatedSequence: 4,
      manifestDigest: "manifest",
      sourceCommit: "commit",
      channel: "Stable",
      targets: [],
      requestId: "request-1",
      trustRoot: "root",
      policyRevision: 1,
      policyFingerprint: "policy",
      hostPlatform: "linux-amd64",
      authorizationKind: "Manual",
    },
  }],
} satisfies HostUpdateStatusResponse;

void recoveryRequiredStatus;
