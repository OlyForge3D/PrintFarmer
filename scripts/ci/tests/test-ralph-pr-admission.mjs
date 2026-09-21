import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {
  acknowledgeLocalJob, clearStrandedKickoff, failLocalKickoff, reserveJob, reserveLocalJob, reserveLocalPrRecovery, recoverLocalReservation,
} from '../ralph-macos-ssh.mjs';

const repository = 'OlyForge3D/PrintFarmer';
const host = 'fixture-windows';
const headSha = 'a'.repeat(40);
const files = ['src/Web/ReactApp/scripts/test-typecheck-baseline.json'];
const job = (jobId = 'recovery-2897', issue = 2799) => ({
  jobId, repository, issue, owner: 'parker', baseSha: 'b'.repeat(40), expectedHost: host,
  model: 'gpt-5.6-terra', effort: 'medium', agent: 'squad',
  acceptanceCriteria: ['Correct accepted finding R1 on the existing PR branch'],
});
const recovery = (pr = 2897, changedFiles = files) => ({
  pr, headSha, files: changedFiles, findings: ['R1: accepted current-head review URL'], scope: 'general',
});
const ownership = (pr = 2897) => ({
  host, pr, headSha, state: 'inactive', observedAt: new Date().toISOString(), source: 'fixture current App/worker/branch reconciliation',
  liveInventoryChecked: true, archivedHistoryChecked: true, terminalHistoryChecked: true, queueChecked: true,
  noPendingDelivery: true, externalClaimsChecked: true, remoteOwnership: 'clear', prerequisitesSatisfied: true,
  activeJobs: [], externalClaims: [],
});
const pull = (pr = 2897, linkedIssues = [2799], changedFiles = files) => ({
  number: pr, state: 'open', draft: true, parentBlocked: true, headSha,
  sameRepository: true, labels: ['squad'], requiresMac: false, linkedIssues, files: changedFiles,
});
const request = () => ({
  job: job(), recovery: recovery(), ownership: ownership(), expectedGeneration: 0, controllerPid: process.pid,
});

async function fixture(t) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'ralph-pr-admission-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const options = {
    env: { RALPH_ADMISSION_LEDGER_DIR: directory }, platform: 'win32', hostname: host, readPull: async () => pull(),
  };
  const ledger = async () => JSON.parse(await readFile(path.join(directory, 'printfarmer-jobs.json'), 'utf8'));
  return { options, ledger, directory };
}
const code = (expected) => (error) => error.code === expected;

test('rejected draft under blocked parent reserves real recovery and binds acknowledgment identity', async (t) => {
  const f = await fixture(t);
  const result = await reserveLocalPrRecovery(request(), f.options);
  assert.equal(result.reservationCreated, true);
  assert.equal(result.state, 'reserved');
  assert.equal(result.prRecovery.work.pr, 2897);
  assert.equal(result.prRecovery.work.headSha, headSha);
  assert.deepEqual(result.prRecovery.work.findings, recovery().findings);
  assert.deepEqual(result.prRecovery.work.linkedIssues, [2799]);
  assert.equal(result.sessionId, undefined);
  await assert.rejects(() => acknowledgeLocalJob(result.jobId, 'session-1', f.options), code('INVALID_REQUEST'));
  const accepted = await acknowledgeLocalJob(result.jobId, 'session-1', { ...f.options, kickoffVerified: true });
  assert.equal(accepted.sessionId, 'session-1');
  assert.equal(accepted.expectedHost, host);
  assert.equal(accepted.prRecovery.work.pr, 2897);
});

test('new issue reservation still rejects linked PR and blocked eligibility', async (t) => {
  const f = await fixture(t);
  for (const eligibility of [
    { repository, issue: 2799, open: true, exactClaim: true, held: false, blocked: false, linkedPr: true },
    { repository, issue: 2799, open: true, exactClaim: true, held: false, blocked: true, linkedPr: false },
  ]) {
    await assert.rejects(() => reserveLocalJob({ job: job(), eligibility }, f.options), code('INELIGIBLE'));
  }
  await assert.rejects(() => reserveJob({ job: job(), prRecovery: {} }, f.options), code('INVALID_REQUEST'));
});

test('live owner, uncertain remote owner, pending delivery and stale observations do not reserve', async (t) => {
  const f = await fixture(t);
  for (const change of [
    { state: 'live' }, { remoteOwnership: 'unknown' }, { remoteOwnership: 'active' },
    { noPendingDelivery: false }, { externalClaimsChecked: false }, { host: 'another-host' },
    { observedAt: new Date(Date.now() - 61_000).toISOString() }, { prerequisitesSatisfied: false },
  ]) {
    await assert.rejects(() => reserveLocalPrRecovery({
      ...request(), ownership: { ...ownership(), ...change },
    }, f.options), code('INVALID_PR_RECOVERY'));
  }
});

test('fresh GitHub head, issue linkage, holds, host scope and full file coverage gate admission', async (t) => {
  const f = await fixture(t);
  for (const change of [
    { headSha: 'c'.repeat(40) }, { labels: ['squad', 'do-not-merge'] }, { labels: [] },
    { sameRepository: false }, { state: 'closed' }, { linkedIssues: [999] },
    { files: [...files, 'renamed-old-path.ts'] }, { requiresMac: true },
  ]) {
    await assert.rejects(() => reserveLocalPrRecovery(request(), {
      ...f.options, readPull: async () => ({ ...pull(), ...change }),
    }), code('STALE_PR'));
  }
  await assert.rejects(() => reserveLocalPrRecovery(request(), { ...f.options, platform: 'darwin' }), code('WRONG_PLATFORM'));
  await assert.rejects(() => reserveLocalPrRecovery({
    ...request(), recovery: { ...recovery(), scope: 'mobile' },
  }, f.options), code('INVALID_PR_RECOVERY'));
});

test('generation CAS and exact duplicate retry cannot authorize a second delivery', async (t) => {
  const f = await fixture(t);
  await reserveLocalPrRecovery(request(), f.options);
  await assert.rejects(() => reserveLocalPrRecovery(request(), f.options), code('STALE_LEDGER'));
  const replay = await reserveLocalPrRecovery({ ...request(), expectedGeneration: 1 }, f.options);
  assert.equal(replay.reservationCreated, false);
  assert.equal(replay.fence, 1);
  assert.equal((await f.ledger()).generation, 1);
  assert.equal(Object.keys((await f.ledger()).jobs).length, 1);
  await assert.rejects(() => reserveLocalPrRecovery({
    ...request(), expectedGeneration: 1, recovery: { ...recovery(), findings: ['different findings'] },
  }, f.options), code('FENCED'));
});

test('concurrent reserve attempts produce exactly one winner', async (t) => {
  const f = await fixture(t);
  const results = await Promise.allSettled([
    reserveLocalPrRecovery(request(), f.options),
    reserveLocalPrRecovery({ ...request(), job: job('second-attempt') }, f.options),
  ]);
  assert.equal(results.filter((result) => result.status === 'fulfilled').length, 1);
  assert.equal(Object.keys((await f.ledger()).jobs).length, 1);
});

test('existing local or uncertain remote jobs remain owned even with claimed local absence', async (t) => {
  const f = await fixture(t);
  await reserveJob({
    job: job('remote-job'), mode: 'remote',
    eligibility: { repository, issue: 2799, open: true, exactClaim: true, held: false, blocked: false, linkedPr: false },
  }, f.options);
  await assert.rejects(() => reserveLocalPrRecovery({ ...request(), expectedGeneration: 1 }, f.options), code('PR_OWNED'));
  assert.equal((await f.ledger()).jobs['remote-job'].state, 'reserved');
});

test('same PR with different issue linkage and another shared baseline owner are fenced', async (t) => {
  const f = await fixture(t);
  const first = await reserveLocalPrRecovery(request(), f.options);
  for (const pr of [2897, 2898]) {
    await assert.rejects(() => reserveLocalPrRecovery({
      job: job(`next-${pr}`, 2800), recovery: recovery(pr), ownership: {
        ...ownership(pr),
        activeJobs: [{ jobId: first.jobId, fence: first.fence, host, files }],
      }, expectedGeneration: 1, controllerPid: process.pid,
    }, { ...f.options, readPull: async () => pull(pr, [2800]) }), code(pr === 2897 ? 'PR_OWNED' : 'FILES_OWNED'));
  }
});

test('an active recovery fences every linked issue and the same PR after its head moves', async (t) => {
  const f = await fixture(t);
  await reserveLocalPrRecovery(request(), { ...f.options, readPull: async () => pull(2897, [2799, 2800]) });
  await assert.rejects(() => reserveLocalJob({
    job: job('new-issue-work', 2800),
    eligibility: { repository, issue: 2800, open: true, exactClaim: true, held: false, blocked: false, linkedPr: false, scope: 'general', classificationComplete: true, filesComplete: true, files: ['src/general.cs'] },
  }, f.options), code('ISSUE_OWNED'));
  const moved = 'd'.repeat(40);
  await assert.rejects(() => reserveLocalPrRecovery({
    ...request(), job: job('new-head-repair'), expectedGeneration: 1,
    recovery: { ...recovery(), headSha: moved }, ownership: { ...ownership(), headSha: moved },
  }, { ...f.options, readPull: async () => ({ ...pull(), headSha: moved }) }), code('PR_OWNED'));
});

test('noncanonical file scopes fail closed and Windows case aliases still conflict', async (t) => {
  const f = await fixture(t);
  for (const file of ['*', 'src/*', 'src\\file.cs', '/src/file.cs', './src/file.cs', 'src/../file.cs', 'src//file.cs', 'src/']) {
    await assert.rejects(() => reserveLocalPrRecovery({
      ...request(), recovery: recovery(2897, [file]),
    }, f.options), code('INVALID_PR_RECOVERY'));
  }
  await assert.rejects(() => reserveLocalPrRecovery({
    ...request(), ownership: {
      ...ownership(), externalClaims: [{
        host, sessionId: 'case-owner', source: 'fixture live inventory', issues: [],
        files: files.map((file) => file.toUpperCase()),
      }],
    },
  }, f.options), code('EXTERNAL_OWNERSHIP'));
});

test('stranded recovery fences all closing issues until its real session is reconciled', async (t) => {
  const f = await fixture(t);
  await reserveLocalPrRecovery(request(), { ...f.options, readPull: async () => pull(2897, [2799, 2800]) });
  await failLocalKickoff(job().jobId, {
    ...f.options, controllerPid: process.pid, sessionId: 'stranded-recovery', kickoffUnverified: true,
  });
  const before = await f.ledger();
  await assert.rejects(() => reserveLocalJob({
    job: job('secondary-issue', 2800),
    eligibility: { repository, issue: 2800, open: true, exactClaim: true, held: false, blocked: false, linkedPr: false, scope: 'general', classificationComplete: true, filesComplete: true, files: ['src/general.cs'] },
  }, f.options), code('STRANDED_SESSION'));
  const next = {
    ...request(), job: job('another-pr', 2801), recovery: recovery(2898),
    ownership: ownership(2898), expectedGeneration: before.generation,
  };
  const options = { ...f.options, readPull: async () => pull(2898, [2800, 2801]) };
  await assert.rejects(() => reserveLocalPrRecovery(next, options), code('STRANDED_SESSION'));
  assert.deepEqual(await f.ledger(), before);
  await clearStrandedKickoff(job().jobId, { ...f.options, sessionAbsent: true });
  const admitted = await reserveLocalPrRecovery({ ...next, expectedGeneration: (await f.ledger()).generation }, options);
  assert.equal(admitted.reservationCreated, true);
});

test('ownership freshness is rechecked after collecting GitHub facts, before admission', async (t) => {
  const f = await fixture(t);
  const input = request();
  await assert.rejects(() => reserveLocalPrRecovery(input, {
    ...f.options, readPull: async () => {
      input.ownership.observedAt = new Date(Date.now() - 61_000).toISOString();
      return pull();
    },
  }), code('INVALID_PR_RECOVERY'));
});

test('independent complete file scopes can run in parallel through the same authority', async (t) => {
  const f = await fixture(t);
  const first = await reserveLocalPrRecovery(request(), f.options);
  const nextFiles = ['src/unrelated.cs'];
  const result = await reserveLocalPrRecovery({
    job: job('independent', 2800), recovery: recovery(2898, nextFiles), ownership: {
      ...ownership(2898), activeJobs: [{ jobId: first.jobId, fence: first.fence, host, files }],
    }, expectedGeneration: 1, controllerPid: process.pid,
  }, { ...f.options, readPull: async () => pull(2898, [2800], nextFiles) });
  assert.equal(result.reservationCreated, true);
  assert.equal(Object.keys((await f.ledger()).jobs).length, 2);
});

test('five existing ledger reservations cannot be bypassed by PR admission', async (t) => {
  const f = await fixture(t);
  const activeJobs = [];
  for (let index = 0; index < 5; index++) {
    const entry = await reserveLocalJob({
      job: job(`occupied-${index}`, 4000 + index),
      eligibility: { repository, issue: 4000 + index, open: true, exactClaim: true, held: false, blocked: false, linkedPr: false, scope: 'general', classificationComplete: true, filesComplete: true, files: [`src/other-${index}.cs`] },
    }, f.options);
    activeJobs.push({ jobId: entry.jobId, fence: entry.fence, host, files: [`src/other-${index}.cs`] });
  }
  await assert.rejects(() => reserveLocalPrRecovery({
    ...request(), expectedGeneration: 5, ownership: { ...ownership(), activeJobs },
  }, f.options), code('SLOT_EXHAUSTED'));
  assert.equal(Object.keys((await f.ledger()).jobs).length, 5);
});

test('untracked live work consumes union capacity and external overlapping files block', async (t) => {
  const f = await fixture(t);
  const claims = Array.from({ length: 5 }, (_, index) => ({
    host, sessionId: `external-${index}`, source: 'fixture actual live inventory',
    issues: [3000 + index], files: [`src/other-${index}.cs`],
  }));
  await assert.rejects(() => reserveLocalPrRecovery({
    ...request(), ownership: { ...ownership(), externalClaims: claims },
  }, f.options), code('SLOT_EXHAUSTED'));
  await assert.rejects(() => reserveLocalPrRecovery({
    ...request(), ownership: { ...ownership(), externalClaims: [{ ...claims[0], files }] },
  }, f.options), code('EXTERNAL_OWNERSHIP'));
});

test('crash before acknowledgment uses existing conservative reservation recovery, not a success receipt', async (t) => {
  const f = await fixture(t);
  await reserveLocalPrRecovery(request(), f.options);
  await assert.rejects(() => recoverLocalReservation(job().jobId, {
    ...f.options, sessionAbsent: true, now: Date.now() + 120_000, isOwnerAlive: () => true,
  }), code('RESERVATION_ACTIVE'));
  const failed = await recoverLocalReservation(job().jobId, {
    ...f.options, sessionAbsent: true, now: Date.now() + 120_000, isOwnerAlive: () => false,
  });
  assert.equal(failed.state, 'failed');
  assert.equal(failed.failureReason, 'session-creation-absent');
  assert.equal(failed.prRecovery.work.pr, 2897);
});

test('unlinked PR recovery does not invent an issue and ignores unrelated caller metadata', async (t) => {
  const f = await fixture(t);
  const unlinked = job();
  delete unlinked.issue;
  const result = await reserveLocalPrRecovery({
    ...request(), job: { ...unlinked, unrelatedMetadata: 'not stored' },
    ownership: { ...ownership(), unrelatedMetadata: 'not stored' },
  }, { ...f.options, readPull: async () => pull(2897, []) });
  assert.equal(result.issue, undefined);
  assert.deepEqual(result.prRecovery.work.linkedIssues, []);
  assert.equal(JSON.stringify(await f.ledger()).includes('not stored'), false);
});
