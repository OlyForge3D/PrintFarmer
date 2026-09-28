import { topologyFor } from './topologies.mjs';

export const hostUpdateExitCodes = Object.freeze({
  refused: 6,
  needsOperator: 10,
});

export function activeServiceDigestExpectations(cell, priorImages) {
  const topology = topologyFor(cell.topology);
  return topology.activeServiceIds(cell.workers).map((serviceId) => {
    const image = priorImages?.[serviceId];
    if (!image?.indexDigest) {
      throw new Error(`missing_prior_image_digest:${serviceId}`);
    }
    return {
      serviceId,
      composeServiceName: topology.composeServiceName(serviceId),
      expectedDigest: image.indexDigest,
    };
  });
}

export function assertActiveServiceDigests({ cell, priorImages, inspectDigest }) {
  const checked = [];
  for (const expected of activeServiceDigestExpectations(cell, priorImages)) {
    const actualDigest = inspectDigest(expected.composeServiceName);
    checked.push({ ...expected, actualDigest });
    if (actualDigest !== expected.expectedDigest) {
      throw new Error(
        `running_digest_mismatch:${expected.serviceId}:compose=${expected.composeServiceName}:expected=${expected.expectedDigest}:actual=${actualDigest}`,
      );
    }
  }
  return checked;
}

export function assertExpectedRefusal(failure, expected) {
  if (failure.exitCode !== hostUpdateExitCodes.refused) {
    throw new Error(`refusal_exit_code_mismatch:expected=${hostUpdateExitCodes.refused}:actual=${failure.exitCode}`);
  }
  if (failure.actual !== expected.outcome || failure.reason !== expected.reason) {
    throw new Error(`unexpected_refusal:${failure.actual}:${failure.reason}`);
  }
}

export function assertExpectedNeedsOperator(preview, expectedReason) {
  if (preview.exitCode !== hostUpdateExitCodes.needsOperator) {
    throw new Error(`needs_operator_exit_code_mismatch:expected=${hostUpdateExitCodes.needsOperator}:actual=${preview.exitCode}`);
  }
  if (!preview.stdout.includes('plan.kind: NeedsOperator') || !preview.stdout.includes(expectedReason)) {
    throw new Error(`needs_operator_evidence_unavailable:${expectedReason}`);
  }
}

export function assertNoMutation(label, before, after) {
  for (const field of ['serviceDigests', 'migrationHeads', 'volumeHashes', 'hostState']) {
    const expected = JSON.stringify(before?.[field]);
    const actual = JSON.stringify(after?.[field]);
    if (expected !== actual) {
      throw new Error(`${label}-${field}-changed:expected=${expected}:actual=${actual}`);
    }
  }
}
