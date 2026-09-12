import { appendFileSync } from 'node:fs';
import {
  admit, advance, allocationKey, hash, requireThat, reserve, transact, verifyConsumer, verifyTag,
  identityLabels, parseTag, verifyProtectionEvidence,
} from './release-policy.mjs';
import {
  branchHead, command, ensureSourceTag, githubClient, gitLedger, readTag, readVersion, verifyProtection,
} from './release-github.mjs';
import { emitBuildIdentity } from './release-metadata.mjs';
import {
  privateSetPath, publicAuthorization, readPrivateAuthorization, readPrivateJson, verifyAuthorization, writeAuthorization,
} from './release-authorization.mjs';

export function runContext(env = process.env) {
  const ref = env.GITHUB_REF;
  return {
    repository: env.GITHUB_REPOSITORY, event: env.GITHUB_EVENT_NAME,
    ref, eventSha: env.GITHUB_SHA,
    workflowIdentity: env.GITHUB_WORKFLOW_REF, workflowSha: env.GITHUB_WORKFLOW_SHA,
    workflowBranch: ref?.replace('refs/heads/', ''),
    buildId: env.GITHUB_RUN_ID, buildAttempt: env.GITHUB_RUN_ATTEMPT,
    channel: env.RELEASE_CHANNEL,
    stage: env.RELEASE_STAGE && env.RELEASE_STAGE !== 'none' ? env.RELEASE_STAGE : undefined,
    requestedTag: env.RELEASE_TAG || undefined,
  };
}

function output(name, value) {
  requireThat(!String(value).includes('\n'), 'Multiline workflow output rejected');
  if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, `${name}=${value}\n`);
}

export async function runReleaseControl(operation, env = process.env, verify = command) {
  requireThat(['admit', 'authorize', 'consume', 'advance'].includes(operation), 'Unknown release operation');
  const writes = ['authorize', 'advance'].includes(operation);
  if (writes) {
    requireThat(env.RELEASE_PUBLISHER_TOKEN && env.RELEASE_PUBLISHER_TOKEN !== env.GH_TOKEN,
      'Protected publisher App token required; github.token cannot verify Administration or publish');
  }
  const api = githubClient(writes ? env.RELEASE_PUBLISHER_TOKEN : env.GH_TOKEN);
  const context = runContext(env);
  const store = gitLedger(api, env.RELEASE_LEDGER_ANCHOR);
  if (['admit', 'authorize'].includes(operation)) {
    const channel = context.event === 'schedule' ? 'insider' : context.channel;
    const branch = channel === 'stable' ? 'main' : 'development';
    const { state } = await store.read();
    const selectedHead = await branchHead(api, branch);
    const admission = admit(context, selectedHead, await readVersion(api, selectedHead),
      state.pointers.stable?.canonicalVersion || state.lastHistoricalStable);
    const checks = await api(`commits/${selectedHead}/check-runs?per_page=100`);
    requireThat(checks.total_count <= 100, 'Check evidence truncated');
    for (const required of ['CI tooling tests', '.NET build', 'Frontend build & tests']) {
      const matching = checks.check_runs.filter(check => check.name === required &&
        check.app?.slug === 'github-actions').sort((a, b) => b.id - a.id);
      requireThat(matching[0]?.conclusion === 'success', `Missing successful exact-SHA qualification: ${required}`);
    }
    requireThat(await branchHead(api, branch) === selectedHead, 'HEAD drift before authorization');
    output('source_sha', selectedHead);
    output('channel', admission.channel);
    if (operation === 'admit') return;
    const protection = await verifyProtection(api, admission.channel, env.RELEASE_PUBLISHER_APP_ID);
    const record = await transact(store, async state => {
      requireThat(await branchHead(api, branch) === selectedHead, 'HEAD drift during allocation retry');
      const existing = state.reservations[allocationKey(admission)];
      if (existing?.identitySha256) {
        // A lost private artifact cannot be reconstructed from public ledger data.
        const saved = readPrivateAuthorization();
        requireThat(hash(saved) === existing.identitySha256, 'Original authorization unavailable; rerun with a new attempt');
        return saved;
      }
      const reservation = reserve(state, admission, new Date().toISOString(), protection);
      verifyProtectionEvidence(reservation.record.protection, admission.channel);
      if (context.requestedTag) requireThat(reservation.record.sourceTag === context.requestedTag,
        'Requested tag is not the durable reservation; omit version to allocate');
      writeAuthorization(reservation.record);
      return reservation.record;
    });
    await ensureSourceTag(api, store, record, transact);
    writeAuthorization(record);
    output('public_identity', JSON.stringify(publicAuthorization(record)));
    return;
  }

  const record = verifyAuthorization(env, verify);
  const { state } = await store.read();
  const entry = state.reservations[record.allocationKey];
  requireThat(entry, 'Unknown release authorization');
  verifyConsumer(record, entry.record, context, entry.identitySha256);
  verifyProtectionEvidence(record.protection, record.channel);
  requireThat(Date.parse(record.protection.verifiedAt) <= Date.parse(record.created),
    'Protection evidence postdates authorization');
  verifyTag(record, entry.tagObject, await readTag(api, record.sourceTag));
  requireThat(parseTag(record.sourceTag).baseVersion ===
    (await readVersion(api, record.sourceCommit)).replace(/\r?\n$/, '').slice(1), 'Source VERSION changed');
  if (operation === 'consume') {
    emitBuildIdentity(record);
    output('version', record.sourceTag);
    output('container_version', record.canonicalVersion);
    output('channel', record.channel);
    output('identity_hash', hash(record));
    output('labels', Object.entries(identityLabels(record)).map(([key, value]) => `${key}=${value}`).join('\\n'));
    output('source_archive_url', `https://github.com/${record.repository}/releases/download/${record.sourceTag}/PrintFarmer-${record.sourceTag}-source.tar.gz`);
    const component = env.RELEASE_COMPONENT;
    requireThat(!component || ['api', 'frontend', 'printer-discovery', 'slicer-host', 'orcaslicer-worker'].includes(component),
      'Unknown release component');
    output('sbom_url', `https://github.com/${record.repository}/releases/download/${record.sourceTag}/printfarmer-${component ? `${component}-` : ''}${record.sourceTag}.spdx.json`);
  } else if (operation === 'advance') {
    const set = readPrivateJson(privateSetPath);
    const expectedPointer = state.pointers[record.channel]?.setHash || '';
    await transact(store, async latest => advance(latest, record, set,
      await branchHead(api, record.sourceBranch), expectedPointer));
    output('set_hash', hash(set));
  } else {
    throw new Error(`Unknown operation: ${operation}`);
  }
}

if (process.argv[1]?.endsWith('release-control.mjs')) {
  runReleaseControl(process.argv[2]).catch(error => { console.error(error.message); process.exitCode = 1; });
}
