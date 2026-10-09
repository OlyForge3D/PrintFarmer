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

export const queueConsumerNames = Object.freeze([
  'autoDispatch',
  'queueOutboxPublisher',
  'backendStartCommandConsumer',
  'backendControlCommandConsumer',
  'queueReconciliation',
  'queueRetentionPrune',
  'bedClearAcknowledgementExpiry',
  'dispatchEscalation',
]);

// The main API's /health exposes a `queue-consumers` entry (#3157) whose numeric
// HealthStatus is 2 (Healthy) only when every durable consumer is running.
export function evaluateQueueConsumersHealth(body) {
  let parsed;
  try {
    parsed = JSON.parse(body);
  } catch {
    return { ok: false, reason: 'queue_consumers_not_exposed', detail: 'health-not-json' };
  }
  const entries = parsed?.results ?? parsed?.entries;
  const entry = entries && typeof entries === 'object' ? entries['queue-consumers'] : undefined;
  if (!entry || typeof entry !== 'object') {
    return { ok: false, reason: 'queue_consumers_not_exposed', detail: 'entry-missing' };
  }
  const data = entry.data && typeof entry.data === 'object' ? entry.data : {};
  const notRunning = queueConsumerNames
    .filter((name) => data[name] !== 'running')
    .map((name) => `${name}=${data[name] ?? 'missing'}`);
  if (entry.status !== 2 || notRunning.length > 0) {
    return {
      ok: false,
      reason: 'queue_consumers_not_running',
      detail: `status=${entry.status}${notRunning.length > 0 ? `;${notRunning.join(',')}` : ''}`,
    };
  }
  return { ok: true, consumers: [...queueConsumerNames] };
}
