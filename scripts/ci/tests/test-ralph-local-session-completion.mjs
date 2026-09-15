import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import test from 'node:test';
import { recordLocalSessionCompletion, recordLocalTerminalResult, reserveLocalJob } from '../ralph-macos-ssh.mjs';

const repository = 'OlyForge3D/PrintFarmer';
const sessionId = '9667f607-58da-47c2-a2ea-90ee64198dc9';
const jobId = 'ralph-2720-handoff-20260915';
const headSha = 'b'.repeat(40);
const timestamp = (ago) => new Date(Date.now() - ago).toISOString();

async function fixture(t) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'ralph-app-completion-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const ledgerFile = path.join(root, 'printfarmer-jobs.json');
  const sessionStateRoot = path.join(root, 'sessions');
  const journalFile = path.join(sessionStateRoot, sessionId, 'events.jsonl');
  await mkdir(path.dirname(journalFile), { recursive: true });
  const entry = {
    jobId, sessionId, fence: 198, issue: 2720, repository, owner: 'parker',
    state: 'accepted', mode: 'local', local: true, baseSha: 'a'.repeat(40),
    requestDigest: 'c'.repeat(64), createdAt: timestamp(20_000), updatedAt: timestamp(20_000),
  };
  const ledger = { version: 1, repository, generation: 206, jobs: {
    [jobId]: entry,
    retained: { jobId: 'retained', issue: 2661, mode: 'local', local: true, state: 'accepted', fence: 205, sessionId: randomUUID() },
    retired: { jobId: 'retired', issue: 2582, state: 'failed', fence: 146, workerAttestation: { dispatchFenced: true }, audit: 'unchanged' },
  } };
  const events = [
    { type: 'session.start', data: { producer: 'copilot-agent', sessionId, context: { cwd: root } } },
    { type: 'assistant.turn_start', data: { turnId: 'turn-1' } },
    { type: 'session.task_complete', data: { success: true, summary: 'Delivered; this prose is not the authentication source.' } },
    { type: 'assistant.turn_end', data: { turnId: 'turn-1' } },
    { type: 'session.usage_checkpoint', data: {} },
  ].map((event, index) => ({ ...event, id: randomUUID(), timestamp: timestamp(10_000 - index) }));
  events.forEach((event, index) => { if (index) event.parentId = events[index - 1].id; });
  const result = {
    jobId, sessionId, fence: 198, headSha, taskCompleteEventId: events[2].id, turnEndEventId: events[3].id,
    publicationRef: 'refs/pull/2722/head', workingTreeClean: true, allCommitsPushed: true,
    validationEvidence: { headSha, passed: true, source: 'Exact-head targeted suite and CI run verified by controller.' },
  };
  const writeJournal = async () => {
    const bytes = events.map((event) => JSON.stringify(event)).join('\n') + '\n';
    await writeFile(journalFile, bytes);
    result.observation = {
      repository, issue: 2720, jobId, fence: 198, sessionId,
      observedAt: timestamp(0), source: 'Fresh host app inventory: idle, stopped, no queued follow-up; exclusive reconciliation owner.',
      running: false, followUpPending: false,
      journalSha256: createHash('sha256').update(bytes).digest('hex'), runtimeHeadEventId: events.at(-1).id,
    };
  };
  const saveLedger = () => writeFile(ledgerFile, JSON.stringify(ledger) + '\n');
  await saveLedger();
  await writeJournal();
  const git = {
    'remote get-url origin': 'https://github.com/OlyForge3D/PrintFarmer.git',
    [`merge-base --is-ancestor ${entry.baseSha} ${headSha}`]: '',
    [`ls-remote --exit-code origin ${result.publicationRef}`]: `${headSha}\t${result.publicationRef}\n`,
    'rev-parse HEAD': headSha, 'status --porcelain=v1 --untracked-files=all': '',
  };
  const options = {
    env: { RALPH_ADMISSION_LEDGER_DIR: root }, sessionStateRoot,
    execFile: async (command, args, executionOptions) => {
      assert.equal(command, 'git');
      assert.deepEqual(args.slice(0, 2), ['-C', root]);
      assert.equal(executionOptions.env.GIT_TERMINAL_PROMPT, '0');
      const key = args.slice(2).join(' ');
      assert.ok(Object.hasOwn(git, key), `Unexpected Git command ${key}`);
      if (git[key] instanceof Error) throw git[key];
      return { stdout: git[key] };
    },
  };
  const readLedger = async () => JSON.parse(await readFile(ledgerFile, 'utf8'));
  const complete = (expectedGeneration = 206) => recordLocalSessionCompletion({ result, expectedGeneration }, options);
  return { root, ledgerFile, journalFile, ledger, entry, events, result, options, git, writeJournal, saveLedger, readLedger, complete };
}

test('app completion records runtime provenance, preserves unrelated audits and fences old job IDs without an exitCode', async (t) => {
  const f = await fixture(t);
  const before = structuredClone(f.ledger);
  const result = await f.complete();
  assert.equal(result.state, 'completed');
  assert.equal(result.exitCode, undefined);
  assert.equal(result.sessionCompletion.kind, 'app-session-task');
  assert.equal(result.sessionCompletion.taskCompleteEventId, f.events[2].id);
  assert.equal(result.sessionCompletion.journalSha256, f.result.observation.journalSha256);
  const ledger = await f.readLedger();
  assert.equal(ledger.generation, 207);
  for (const id of ['retained', 'retired']) assert.deepEqual(ledger.jobs[id], before.jobs[id]);
  assert.equal(result.requestDigest, before.jobs[jobId].requestDigest);
  assert.equal(result.fence, 198);
  await assert.rejects(() => reserveLocalJob({
    job: { jobId, issue: 2720, repository, owner: 'parker', baseSha: f.entry.baseSha,
      expectedHost: 'windows-local', model: 'gpt-5.6-terra', effort: 'medium', agent: 'squad', acceptanceCriteria: [] },
    eligibility: { repository, issue: 2720, open: true, exactClaim: true, held: false, blocked: false, linkedPr: false },
  }, f.options), (error) => error.code === 'FENCED');
});

test('exact replay is audit-only and idempotent even after later runtime activity; changed proof is rejected', async (t) => {
  const f = await fixture(t);
  const first = await f.complete();
  await writeFile(f.journalFile, 'Later runtime content is not reread for historical replay.\n');
  f.result.observation.observedAt = timestamp(120_000);
  f.options.execFile = () => assert.fail('Historical replay must not re-verify mutable Git state.');
  assert.deepEqual(await f.complete(207), first);
  assert.equal((await f.readLedger()).generation, 207);
  f.result.taskCompleteEventId = randomUUID();
  await assert.rejects(() => f.complete(207), (error) => error.code === 'INVALID_SESSION_COMPLETION');
});

test('concurrent callers revalidate generation and allow only one completion write', async (t) => {
  const f = await fixture(t);
  const results = await Promise.allSettled([f.complete(), f.complete()]);
  assert.equal(results.filter((result) => result.status === 'fulfilled').length, 1);
  assert.equal(results.find((result) => result.status === 'rejected').reason.code, 'STALE_LEDGER');
  assert.equal((await f.readLedger()).generation, 207);
});

test('reject malformed identity, stale/live observation and insufficient proof without touching ledger or backup', async (t) => {
  const cases = [
    ['wrong fence', (f) => { f.result.fence += 1; }],
    ['wrong session', (f) => { f.result.sessionId = randomUUID(); }],
    ['wrong job', (f) => { f.result.jobId = 'different-job'; }],
    ['no generation', (f) => { f.complete = () => recordLocalSessionCompletion({ result: f.result }, f.options); }],
    ['invented zero', (f) => { f.result.exitCode = 0; }],
    ['failed validation', (f) => { f.result.validationEvidence.passed = false; }],
    ['wrong validation head', (f) => { f.result.validationEvidence.headSha = 'c'.repeat(40); }],
    ['no validation source', (f) => { f.result.validationEvidence.source = ''; }],
    ['dirty assertion', (f) => { f.result.workingTreeClean = false; }],
    ['unpushed assertion', (f) => { f.result.allCommitsPushed = false; }],
    ['missing task', (f) => { f.result.taskCompleteEventId = randomUUID(); }],
    ['missing turn', (f) => { f.result.turnEndEventId = randomUUID(); }],
    ['running app', (f) => { f.result.observation.running = true; }],
    ['pending followup', (f) => { f.result.observation.followUpPending = true; }],
    ['stale observation', (f) => { f.result.observation.observedAt = timestamp(61_000); }],
    ['future observation', (f) => { f.result.observation.observedAt = timestamp(-10_000); }],
    ['wrong journal', (f) => { f.result.observation.journalSha256 = 'a'.repeat(64); }],
    ['wrong runtime head', (f) => { f.result.observation.runtimeHeadEventId = randomUUID(); }],
    ['wrong observation fence', (f) => { f.result.observation.fence = 999; }],
    ['bad publication ref', (f) => { f.result.publicationRef = '--heads'; }],
  ];
  for (const [name, change] of cases) await t.test(name, async (subtest) => {
    const f = await fixture(subtest);
    const before = await readFile(f.ledgerFile, 'utf8');
    change(f);
    await assert.rejects(() => f.complete(), (error) => error.code === 'INVALID_SESSION_COMPLETION');
    assert.equal(await readFile(f.ledgerFile, 'utf8'), before);
    await assert.rejects(() => readFile(`${f.ledgerFile}.bak`), (error) => error.code === 'ENOENT');
  });
});

test('runtime evidence rejects forged prose, failure, wrong identity, chronology and any post-completion work', async (t) => {
  const cases = [
    ['caller prose only', (f) => { f.events[2].type = 'assistant.message'; }],
    ['failed task', (f) => { f.events[2].data.success = false; }],
    ['wrong runtime session', (f) => { f.events[0].data.sessionId = randomUUID(); }],
    ['wrong producer', (f) => { f.events[0].data.producer = 'caller'; }],
    ['wrong turn', (f) => { f.events[3].data.turnId = 'different-turn'; }],
    ['broken chain', (f) => { f.events[3].parentId = randomUUID(); }],
    ['duplicate event ID', (f) => { f.events[4].id = f.events[3].id; }],
    ['task before admission', (f) => { f.entry.createdAt = timestamp(5_000); }],
    ['invalid admission time', (f) => { f.entry.updatedAt = 'unknown'; }],
    ['newer terminal predecessor', (f) => { f.ledger.jobs.predecessor = { sessionId, state: 'completed', updatedAt: timestamp(5_000) }; }],
    ['missing predecessor time', (f) => { f.ledger.jobs.predecessor = { sessionId, state: 'completed' }; }],
    ['other active session admission', (f) => { f.ledger.jobs.predecessor = { sessionId, state: 'accepted', updatedAt: timestamp(30_000) }; }],
    ['later user input', (f) => { f.events[4].type = 'user.message'; }],
    ['later turn start', (f) => { f.events[4].type = 'assistant.turn_start'; }],
    ['later tool activity', (f) => { f.events[4].type = 'tool.execution_start'; }],
    ['unknown lifecycle event', (f) => { f.events[4].type = 'session.unknown'; }],
    ['pending hook', (f) => { f.events[4].type = 'hook.start'; f.events[4].data.hookInvocationId = 'pending'; }],
  ];
  for (const [name, change] of cases) await t.test(name, async (subtest) => {
    const f = await fixture(subtest);
    change(f);
    await f.saveLedger();
    await f.writeJournal();
    const before = await readFile(f.ledgerFile, 'utf8');
    await assert.rejects(() => f.complete(), (error) => error.code === 'INVALID_SESSION_COMPLETION');
    assert.equal(await readFile(f.ledgerFile, 'utf8'), before);
  });
});

test('missing or partially written journals retain accounting', async (t) => {
  for (const content of ['', '{"id":']) await t.test(JSON.stringify(content), async (subtest) => {
    const f = await fixture(subtest);
    await writeFile(f.journalFile, content);
    await assert.rejects(() => f.complete(), (error) => error.code === 'INVALID_SESSION_COMPLETION');
    assert.equal((await f.readLedger()).generation, 206);
  });
});

test('Git verification rejects wrong origin, ancestry, HEAD, publication, dirty work and unavailable evidence', async (t) => {
  const cases = [
    ['remote get-url origin', 'https://github.com/other/repo.git'],
    [`merge-base --is-ancestor ${'a'.repeat(40)} ${headSha}`, new Error('not ancestor')],
    ['rev-parse HEAD', 'c'.repeat(40)],
    ['ls-remote --exit-code origin refs/pull/2722/head', 'c'.repeat(40) + '\trefs/pull/2722/head\n'],
    ['ls-remote --exit-code origin refs/pull/2722/head', new Error('network unavailable')],
    ['status --porcelain=v1 --untracked-files=all', '?? unpublished.txt\n'],
  ];
  for (const [command, output] of cases) await t.test(command, async (subtest) => {
    const f = await fixture(subtest);
    f.git[command] = output;
    await assert.rejects(() => f.complete(), (error) => error.code === 'INVALID_SESSION_COMPLETION');
    assert.equal((await f.readLedger()).generation, 206);
  });
});

test('journal or observation races during Git verification fail closed under the ledger lock', async (t) => {
  for (const race of ['journal', 'observation']) await t.test(race, async (subtest) => {
    const f = await fixture(subtest);
    const execute = f.options.execFile;
    f.options.execFile = async (...args) => {
      if (args[1].includes('ls-remote')) {
        if (race === 'journal') await writeFile(f.journalFile, await readFile(f.journalFile, 'utf8') + '\n');
        else f.result.observation.observedAt = timestamp(61_000);
      }
      return execute(...args);
    };
    await assert.rejects(() => f.complete(), (error) => error.code === 'INVALID_SESSION_COMPLETION');
    assert.equal((await f.readLedger()).generation, 206);
  });
});

test('existing process terminal-local still requires a real integer and retains its original result shape', async (t) => {
  const f = await fixture(t);
  const result = { jobId, sessionId, headSha, workingTreeClean: true, allCommitsPushed: true, validationEvidence: 'verified process result' };
  await assert.rejects(() => recordLocalTerminalResult(result, f.options), (error) => error.code === 'INVALID_TERMINAL_EVIDENCE');
  const failed = await recordLocalTerminalResult({ ...result, exitCode: 7 }, f.options);
  assert.equal(failed.state, 'failed');
  assert.equal(failed.exitCode, 7);
  assert.equal(failed.sessionCompletion, undefined);
});

test('CLI exposes the separate app-session command and rejects missing proof without mutating real state', async () => {
  const child = spawn(process.execPath, [path.resolve('scripts', 'ci', 'ralph-admission.mjs'), 'complete-local-session'], { stdio: ['pipe', 'pipe', 'pipe'] });
  let error = '';
  child.stderr.on('data', (chunk) => { error += chunk; });
  child.stdin.end(JSON.stringify({ result: {} }));
  const code = await new Promise((resolve) => child.on('close', resolve));
  assert.equal(code, 1);
  assert.equal(JSON.parse(error).code, 'INVALID_SESSION_COMPLETION');
});
