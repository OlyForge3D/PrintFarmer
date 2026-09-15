import { appendFileSync, closeSync, constants, fstatSync, lstatSync, openSync, realpathSync } from 'node:fs';
import { basename, dirname, isAbsolute, join, resolve } from 'node:path';
import {
  abandon, abandonmentAuthorization, admit, advance, hash, requireThat, reserve, transact, verifyConsumer, verifyTag,
  identityLabels, parseTag, verifyProtectionEvidence, validateReservationAdmission, validateApprovalMode,
} from './release-policy.mjs';
import {
  command, ensureSourceTag, githubClient, gitLedger, readTag, readVersion,
  verifyCanonicalSource, verifyProtection,
  verifyAbandonmentApproval, verifyStableQualification,
} from './release-github.mjs';
import { emitBuildIdentity } from './release-metadata.mjs';
import {
  privateSetPath, publicAuthorization, readPrivateAuthorization, readPrivateJson, readReleaseManifest, verifyAuthorization, writeAuthorization, writePublicSet,
} from './release-authorization.mjs';
import {
  readQualificationReceipt, transactionFromEnvironment,
  verifyTransactionQualification,
} from './release-transaction.mjs';

export function runContext(env = process.env, transaction = transactionFromEnvironment(env)) {
  const ref = env.GITHUB_REF;
  return {
    repository: env.GITHUB_REPOSITORY, event: env.GITHUB_EVENT_NAME,
    ref, eventSha: transaction?.sourceCommit ?? env.GITHUB_SHA,
    sourceCommit: transaction?.sourceCommit ?? env.GITHUB_SHA,
    workflowIdentity: transaction?.workflowIdentity ?? env.GITHUB_WORKFLOW_REF,
    workflowSha: transaction?.workflowCommit ?? env.GITHUB_WORKFLOW_SHA,
    workflowBranch: ref?.replace('refs/heads/', ''),
    observedBranchHead: transaction?.observedBranchHead,
    buildId: transaction?.runId ?? env.GITHUB_RUN_ID,
    buildAttempt: transaction?.runAttempt ?? env.GITHUB_RUN_ATTEMPT,
    channel: transaction?.channel ?? env.RELEASE_CHANNEL,
    stage: env.RELEASE_STAGE && env.RELEASE_STAGE !== 'none' ? env.RELEASE_STAGE : undefined,
    requestedTag: env.RELEASE_TAG || undefined,
  };
}

export function output(name, value) {
  requireThat(typeof name === 'string' && /^[a-z][a-z0-9_]*$/.test(name) && !/[\r\n]/.test(name),
    'Invalid workflow output name');
  const text = String(value);
  requireThat(!/[\r\n]/.test(text), 'Multiline workflow output rejected');
  const path = process.env.GITHUB_OUTPUT;
  if (!path) return;
  const runner = process.env.RUNNER_TEMP;
  requireThat(runner && isAbsolute(runner) && isAbsolute(path), 'Invalid runner output destination');
  const directory = join(realpathSync(runner), '_runner_file_commands');
  requireThat(dirname(resolve(path)) === directory && realpathSync(dirname(path)) === directory &&
    /^set_output_[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/.test(basename(path)) &&
    path === resolve(path) && !/[\r\n]/.test(path),
  'Invalid runner output destination');
  const descriptor = openSync(path, constants.O_WRONLY | constants.O_APPEND | constants.O_NOFOLLOW);
  try {
    const actual = fstatSync(descriptor);
    const named = lstatSync(path);
    requireThat(actual.isFile() && actual.nlink === 1 && named.isFile() && !named.isSymbolicLink() &&
      named.nlink === 1 && actual.dev === named.dev && actual.ino === named.ino,
    'Runner output must be the same single-link regular file');
    appendFileSync(descriptor, `${name}=${text}\n`);
  } finally {
    closeSync(descriptor);
  }
}

export async function runReleaseControl(operation, env = process.env, verify = command) {
  requireThat(['admit', 'authorize', 'consume', 'preflight', 'advance', 'recover-abandonment', 'abandon'].includes(operation), 'Unknown release operation');
  const consumer = ['consume', 'preflight', 'advance'].includes(operation);
  const transaction = transactionFromEnvironment(env);
  const verifyQualification = async (requireReceipt = false) => {
    if (requireReceipt) {
      const receipt = readQualificationReceipt(transaction);
      requireThat(receipt.run.attempt === env.GITHUB_RUN_ATTEMPT,
        'Qualification receipt belongs to another execution attempt');
    }
    return verifyTransactionQualification(transaction, githubClient(env.GH_TOKEN),
      Date.now(), env.GITHUB_RUN_ATTEMPT);
  };
  if (operation === 'abandon') {
    requireThat(transaction.channel === 'insider', 'Only insider release transactions may abandon a reservation');
    requireThat(env.GITHUB_RUN_ATTEMPT === '1',
      'Abandonment is restricted to the initial protected workflow attempt');
  }
  if (['authorize', 'consume', 'preflight', 'advance'].includes(operation)) {
    requireThat(env.RELEASE_SOURCE_COMMIT === transaction.sourceCommit,
      'Release source identity does not match the pinned release transaction');
  }
  const privileged = ['authorize', 'preflight', 'advance', 'recover-abandonment', 'abandon'].includes(operation);
  if (privileged) {
    requireThat(env.RELEASE_PUBLISHER_TOKEN && env.RELEASE_PUBLISHER_TOKEN !== env.GH_TOKEN,
      'Protected publisher App token required; github.token cannot verify Administration or publish');
  }
  const api = githubClient(privileged ? env.RELEASE_PUBLISHER_TOKEN : env.GH_TOKEN);
  const context = runContext(env, transaction);
  if (consumer) {
    requireThat(context.sourceCommit === transaction.sourceCommit,
    'Consumer source identity does not match the pinned release transaction');
  }
  const store = gitLedger(api, env.RELEASE_LEDGER_ANCHOR);
  if (operation === 'recover-abandonment') {
    const target = env.RELEASE_ABANDONMENT_TARGET;
    requireThat(/^[a-f0-9]{64}$/.test(target || ''), 'Immutable abandonment reservation target is missing or malformed');
    const { state } = await store.read();
    const reservation = state.reservations[target];
    requireThat(reservation?.record?.allocationKey === target && reservation.record.channel === 'insider' &&
      reservation.identitySha256 === reservation.record.identitySha256 && !reservation.set && !reservation.abandonment,
    'Immutable abandonment reservation is unavailable, activated, or terminal');
    output('authorization_run_id', reservation.record.buildId);
    output('authorization_attempt', reservation.record.buildAttempt);
    output('source_sha', reservation.record.sourceCommit);
    output('public_identity', JSON.stringify(publicAuthorization(reservation.record)));
    return reservation.record;
  }
  if (['admit', 'authorize'].includes(operation)) {
    validateApprovalMode(env.RELEASE_APPROVAL_MODE);
    if (operation === 'authorize') {
      validateApprovalMode(env.RELEASE_ADMITTED_APPROVAL_MODE);
      requireThat(env.RELEASE_ADMITTED_APPROVAL_MODE === env.RELEASE_APPROVAL_MODE,
        'Approval mode changed after admission; align repository and environment policy and rerun all jobs');
      requireThat(transaction.approvalMode === env.RELEASE_APPROVAL_MODE,
        'Approval mode changed after transaction selection; rerun all jobs');
    }
    const channel = transaction.channel;
    const branch = channel === 'stable' ? 'main' : 'development';
    const { state } = await store.read();
    const selectedHead = transaction.sourceCommit;
    await verifyCanonicalSource(api, branch, selectedHead);
    const admission = admit(context, selectedHead, await readVersion(api, selectedHead));
    validateReservationAdmission(state, admission);
    output('source_sha', context.eventSha);
    output('channel', admission.channel);
    if (operation === 'admit') {
      output('approval_mode', env.RELEASE_APPROVAL_MODE);
      return admission;
    }
    await verifyQualification(true);
    await verifyCanonicalSource(api, branch, selectedHead);
    const protection = await verifyProtection(api, admission.channel, env.RELEASE_PUBLISHER_APP_ID,
      env.RELEASE_APPROVAL_MODE, env.RELEASE_OWNER_APPROVED_REVIEWERS);
    const record = await transact(store, async state => {
      const existing = validateReservationAdmission(state, admission);
      await verifyQualification(true);
      const qualification = await verifyStableQualification(api, state, admission);
      await verifyCanonicalSource(api, branch, selectedHead);
      if (existing?.identitySha256) {
        // A lost private artifact cannot be reconstructed from public ledger data.
        const saved = readPrivateAuthorization();
        requireThat(hash(saved) === existing.identitySha256, 'Original authorization unavailable; rerun with a new attempt');
        requireThat(saved.protection.approvalMode === protection.approvalMode,
          'Approval mode changed after reservation; rerun with a new attempt');
        return saved;
      }
      const reservation = reserve(state, admission, new Date().toISOString(), protection, qualification);
      verifyProtectionEvidence(reservation.record.protection, admission.channel);
      if (context.requestedTag) requireThat(reservation.record.sourceTag === context.requestedTag,
        'Requested tag is not the durable reservation; omit version to allocate');
      writeAuthorization(reservation.record);
      return reservation.record;
    });
    await ensureSourceTag(api, store, record, transact);
    writeAuthorization(record);
    output('public_identity', JSON.stringify(publicAuthorization(record)));
    output('verified_branch_head', await verifyCanonicalSource(api, branch, selectedHead));
    return record;
  }

  const record = verifyAuthorization(env, verify);
  if (operation === 'abandon') requireThat(record.channel === 'insider',
    'Only insider reservations may be abandoned');
  if (operation === 'abandon') requireThat(record.channel === transaction.channel,
    'Abandonment transaction channel does not match the recovered reservation');
  const { state } = await store.read();
  const entry = state.reservations[record.allocationKey];
  requireThat(entry, 'Unknown release authorization');
  requireThat(!entry.abandonment, 'Terminally abandoned reservation cannot be consumed, preflighted, advanced, or recovered');
  requireThat(entry.identitySha256 === hash(record), 'Release authorization differs from the immutable reservation');
  if (operation !== 'abandon') verifyConsumer(record, entry, context, entry.identitySha256);
  verifyProtectionEvidence(record.protection, record.channel);
  requireThat(Date.parse(record.protection.verifiedAt) <= Date.parse(record.created),
    'Protection evidence postdates authorization');
  if (operation !== 'abandon') {
    // Consumer verification binds these public ledger references to the signed artifact.
    verifyTag(record, entry.tagObject, await readTag(api, entry.record.sourceTag));
    requireThat(parseTag(record.sourceTag).baseVersion ===
      (await readVersion(api, entry.record.sourceCommit)).replace(/\r?\n$/, '').slice(1), 'Source VERSION changed');
  }
  if (operation === 'consume') {
    const metadata = emitBuildIdentity(record);
    output('frontend_identity', metadata.frontendIdentity);
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
  } else if (operation === 'preflight') {
    requireThat(transaction.sourceCommit === record.sourceCommit,
      'Publication preflight transaction binding mismatch');
    const currentBranchHead = await verifyCanonicalSource(api, record.sourceBranch, record.sourceCommit);
    await verifyQualification();
    await verifyProtection(api, record.channel, env.RELEASE_PUBLISHER_APP_ID,
      env.RELEASE_APPROVAL_MODE, env.RELEASE_OWNER_APPROVED_REVIEWERS);
    const expectedPointer = state.pointers[record.channel]?.manifestEnvelopeSha256 || '';
    output('verified_branch_head', currentBranchHead);
    output('expected_pointer', expectedPointer);
  } else if (operation === 'abandon') {
    requireThat(env.RELEASE_ABANDONMENT_TARGET === record.allocationKey,
      'Immutable abandonment reservation target does not match authorization');
    const protection = await verifyProtection(api, record.channel, env.RELEASE_PUBLISHER_APP_ID,
      env.RELEASE_APPROVAL_MODE, env.RELEASE_OWNER_APPROVED_REVIEWERS);
    const approval = await verifyAbandonmentApproval(api, {
      runId: env.GITHUB_RUN_ID, runAttempt: env.GITHUB_RUN_ATTEMPT,
    }, record,
      env.RELEASE_ABANDONMENT_TARGET, env.RELEASE_OWNER_APPROVED_REVIEWERS);
    const authorization = abandonmentAuthorization(record, protection, approval);
    await transact(store, async latest => abandon(latest, record, authorization, protection));
  } else if (operation === 'advance') {
    const set = readPrivateJson(privateSetPath);
    const expectedPointer = env.RELEASE_EXPECTED_POINTER ?? '';
    requireThat((state.pointers[record.channel]?.manifestEnvelopeSha256 || '') === expectedPointer,
      'Channel pointer changed after publication preflight');
    requireThat(transaction.sourceCommit === record.sourceCommit,
      'Pointer transaction binding mismatch');
    await verifyCanonicalSource(api, record.sourceBranch, record.sourceCommit);
    await verifyQualification();
    await verifyProtection(api, record.channel, env.RELEASE_PUBLISHER_APP_ID,
      env.RELEASE_APPROVAL_MODE, env.RELEASE_OWNER_APPROVED_REVIEWERS);
    requireThat(/^[a-f0-9]{40}$/.test(env.RELEASE_VERIFIED_BRANCH_HEAD || ''),
      'Missing publication preflight branch evidence');
    const { serializedManifest, serializedEnvelope } = readReleaseManifest();
    const signed = { serializedManifest, serializedEnvelope };
    await transact(store, async latest => advance(latest, record, set, signed,
      await verifyCanonicalSource(api, record.sourceBranch, record.sourceCommit), expectedPointer));
    output('set_hash', hash(writePublicSet(record, set)));
  } else {
    throw new Error(`Unknown operation: ${operation}`);
  }
}

if (process.argv[1]?.endsWith('release-control.mjs')) {
  runReleaseControl(process.argv[2]).catch(error => { console.error(error.message); process.exitCode = 1; });
}
