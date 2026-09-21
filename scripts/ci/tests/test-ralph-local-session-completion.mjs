import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { mkdir, mkdtemp, readFile, rm, writeFile, open } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import test from 'node:test';
import { activeJobStates, recordLocalSessionCompletion, recordLocalSessionHandoff, recordLocalCompletedHandoff, recordLocalTerminalResult, reserveLocalJob } from '../ralph-macos-ssh.mjs';

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
    { type: 'session.task_complete', data: { success: true, summary: `Delivered ${jobId}, fence 198; prose is not the authentication source.` } },
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
    eligibility: { repository, issue: 2720, open: true, exactClaim: true, held: false, blocked: false, linkedPr: false, scope: 'general', classificationComplete: true, filesComplete: true, files: ['src/general.cs'] },
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
    ['another task report', (f) => { f.events[2].data.summary = 'Delivered ralph-another-job, fence 199'; }],
    ['wrong reported fence', (f) => { f.events[2].data.summary = `Delivered ${jobId}, fence 1980`; }],
    ['job ID prefix collision', (f) => { f.events[2].data.summary = `Delivered ${jobId}-extra, fence 198`; }],
    ['job ID suffix collision', (f) => { f.events[2].data.summary = `Delivered other-${jobId}, fence 198`; }],
    ['fence suffix word', (f) => { f.events[2].data.summary = `Delivered ${jobId}, fence 198extra`; }],
    ['fence suffix dash', (f) => { f.events[2].data.summary = `Delivered ${jobId}, fence 198-extra`; }],
    ['fence suffix underscore', (f) => { f.events[2].data.summary = `Delivered ${jobId}, fence 198_extra`; }],
    ['fractional fence', (f) => { f.events[2].data.summary = `Delivered ${jobId}, fence 198.5`; }],
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
    ['pre-task broken chain', (f) => { f.events[1].parentId = randomUUID(); }],
    ['pre-task timestamp regression', (f) => { f.events[1].timestamp = timestamp(15_000); }],
    ['pre-task unmatched hook end', (f) => { f.events[1].type = 'hook.end'; f.events[1].data.hookInvocationId = 'unknown'; }],
    ['missing hook ID', (f) => { f.events[4].type = 'hook.start'; }],
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

test('pending/duplicate pre-task hooks and already ended turns cannot authorize task completion', async (t) => {
  for (const kind of ['pending-hook', 'duplicate-hook', 'ended-turn']) await t.test(kind, async (subtest) => {
    const f = await fixture(subtest);
    const extra = kind === 'ended-turn'
      ? [{ type: 'assistant.turn_end', data: { turnId: 'turn-1' } }]
      : Array.from({ length: kind === 'duplicate-hook' ? 2 : 1 }, () => ({ type: 'hook.start', data: { hookInvocationId: 'hook-1' } }));
    f.events.splice(2, 0, ...extra.map((event) => ({ ...event, id: randomUUID(), timestamp: f.events[1].timestamp })));
    f.events.forEach((event, index) => { if (index) event.parentId = f.events[index - 1].id; });
    await f.writeJournal();
    await assert.rejects(() => f.complete(), (error) => error.code === 'INVALID_SESSION_COMPLETION');
    assert.equal((await f.readLedger()).generation, 206);
  });
});

test('completed hooks that cross task completion remain verifiable', async (t) => {
  const f = await fixture(t);
  f.events.splice(2, 0, { id: randomUUID(), type: 'hook.start', data: { hookInvocationId: 'cross-task' }, timestamp: f.events[1].timestamp });
  f.events.push({ id: randomUUID(), type: 'hook.end', data: { hookInvocationId: 'cross-task', success: true }, timestamp: f.events.at(-1).timestamp });
  f.events.forEach((event, index) => { if (index) event.parentId = f.events[index - 1].id; });
  await f.writeJournal();
  assert.equal((await f.complete()).state, 'completed');
});

test('an embedded agent cannot close another agent hook', async (t) => {
  const f = await fixture(t);
  f.events.splice(2, 0, { id: randomUUID(), type: 'hook.start', data: { hookInvocationId: 'root-hook' }, timestamp: f.events[1].timestamp });
  f.events.push({ id: randomUUID(), agentId: randomUUID(), type: 'hook.end',
    data: { hookInvocationId: 'root-hook', success: true }, timestamp: f.events.at(-1).timestamp });
  f.events.forEach((event, index) => { if (index) event.parentId = f.events[index - 1].id; });
  await f.writeJournal();
  await assert.rejects(() => f.complete(), (error) => error.code === 'INVALID_SESSION_COMPLETION');
});

test('multiplexed agent turns with repeated turn IDs are independent; only root completion counts', async (t) => {
  for (const kind of ['valid', 'embedded-completion', 'embedded-end', 'pending-agent']) await t.test(kind, async (subtest) => {
    const f = await fixture(subtest);
    const firstAgent = randomUUID();
    const secondAgent = randomUUID();
    const embedded = [
      { agentId: firstAgent, type: 'assistant.turn_start', data: { turnId: 'turn-1' } },
      { agentId: secondAgent, type: 'assistant.turn_start', data: { turnId: 'turn-1' } },
      { agentId: secondAgent, type: 'assistant.turn_end', data: { turnId: 'turn-1' } },
      ...(kind === 'pending-agent' ? [] : [{ agentId: firstAgent, type: 'assistant.turn_end', data: { turnId: 'turn-1' } }]),
    ];
    f.events.splice(2, 0, ...embedded.map((event) => ({ ...event, id: randomUUID(), timestamp: f.events[1].timestamp })));
    if (kind === 'embedded-completion') f.events.find((event) => event.id === f.result.taskCompleteEventId).agentId = firstAgent;
    if (kind === 'embedded-end') f.events.find((event) => event.id === f.result.turnEndEventId).agentId = firstAgent;
    f.events.forEach((event, index) => { if (index) event.parentId = f.events[index - 1].id; });
    await f.writeJournal();
    if (kind === 'valid') assert.equal((await f.complete()).state, 'completed');
    else {
      await assert.rejects(() => f.complete(), (error) => error.code === 'INVALID_SESSION_COMPLETION');
      assert.equal((await f.readLedger()).generation, 206);
    }
  });
});

test('large multi-chunk journals stream successfully without whole-file materialization', async (t) => {
  const f = await fixture(t);
  const file = await open(f.journalFile, 'w');
  const hash = createHash('sha256');
  const write = async (event) => {
    const line = JSON.stringify(event) + '\n';
    hash.update(line);
    await file.write(line);
  };
  try {
    await write(f.events[0]);
    let parentId = f.events[0].id;
    for (let index = 0; index < 128; index += 1) {
      const event = { id: randomUUID(), parentId, timestamp: f.events[0].timestamp,
        type: 'session.info', data: { content: 'x'.repeat(256 * 1024) } };
      await write(event);
      parentId = event.id;
    }
    f.events[1].parentId = parentId;
    for (const event of f.events.slice(1)) await write(event);
  } finally {
    await file.close();
  }
  f.result.observation.journalSha256 = hash.digest('hex');
  f.result.observation.observedAt = timestamp(0);
  assert.equal((await f.complete()).state, 'completed');
});

test('host external-tool receipts correlate by requestId across native causal branches', async (t) => {
  for (const kind of ['valid', 'receipt-parent', 'wrong-receipt', 'pending-request', 'broken-main-chain']) await t.test(kind, async (subtest) => {
    const f = await fixture(subtest);
    const requestId = randomUUID();
    const requested = { id: randomUUID(), parentId: f.events[1].id, type: 'external_tool.requested',
      timestamp: f.events[1].timestamp, data: { requestId } };
    const completed = { id: randomUUID(), parentId: randomUUID(), type: 'external_tool.completed',
      timestamp: f.events[1].timestamp, data: { requestId: kind === 'wrong-receipt' ? randomUUID() : requestId } };
    f.events[2].parentId = kind === 'broken-main-chain' ? randomUUID() : kind === 'receipt-parent' ? completed.id : requested.id;
    f.events.splice(2, 0, requested, ...(kind === 'pending-request' ? [] : [completed]));
    await f.writeJournal();
    if (kind === 'valid' || kind === 'receipt-parent') assert.equal((await f.complete()).state, 'completed');
    else {
      await assert.rejects(() => f.complete(), (error) => error.code === 'INVALID_SESSION_COMPLETION');
      assert.equal((await f.readLedger()).generation, 206);
    }
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

test('another ledger operation succeeds during network verification and fences the stale completion', async (t) => {
  const f = await fixture(t);
  const execute = f.options.execFile;
  f.options.execFile = async (...args) => {
    if (args[1].includes('ls-remote')) {
      await recordLocalTerminalResult({
        jobId: 'retained', sessionId: f.ledger.jobs.retained.sessionId, headSha,
        exitCode: 0, workingTreeClean: true, allCommitsPushed: true, validationEvidence: 'Other job actual process result.',
      }, f.options);
    }
    return execute(...args);
  };
  await assert.rejects(() => f.complete(), (error) => error.code === 'STALE_LEDGER');
  const ledger = await f.readLedger();
  assert.equal(ledger.jobs.retained.state, 'completed');
  assert.equal(ledger.jobs[jobId].state, 'accepted');
  assert.equal(ledger.generation, 207);
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

async function handoffFixture(t) {
  const f = await fixture(t);
  const content = `Historical clean worktree / HEAD check: ${headSha}`;
  const receipt = { id: randomUUID(), type: 'tool.execution_complete', timestamp: f.events[1].timestamp,
    data: { success: true, toolCallId: 'recorded-delivery', result: { content } } };
  f.events.splice(2, 0, { id: randomUUID(), type: 'tool.execution_start', timestamp: f.events[1].timestamp,
    data: { toolCallId: 'recorded-delivery', toolName: 'powershell' } }, receipt);
  f.events.find((event) => event.id === f.result.taskCompleteEventId).data.summary =
    `${jobId}, fence 198. Working tree clean; all commits pushed.`;
  const assignment = { id: randomUUID(), type: 'user.message', timestamp: timestamp(5_000),
    data: { content: 'Implement new scoped issue #2724, preserve prior completed work.', source: `agent-${randomUUID()}`, interactionId: randomUUID() } };
  const activity = { id: randomUUID(), type: 'assistant.turn_start', timestamp: timestamp(4_000),
    data: { turnId: 'new-task', interactionId: assignment.data.interactionId } };
  f.events.push(assignment, activity);
  f.events.forEach((event, index) => { if (index) event.parentId = f.events[index - 1].id; });
  const successor = {
    sessionId, job: { jobId: 'ralph-2724-handoff', repository, issue: 2724, owner: 'parker', baseSha: 'a'.repeat(40),
      expectedHost: 'windows-local', model: 'gpt-5.6-terra', effort: 'medium', agent: 'squad',
      acceptanceCriteria: ['Correct provenance collector without weakening trust.'] },
    assignmentEventId: assignment.id, activityEventId: activity.id, assignmentSource: assignment.data.source,
    assignmentContentSha256: createHash('sha256').update(assignment.data.content).digest('hex'),
    deliveryEventId: receipt.id, deliveryContentSha256: createHash('sha256').update(content).digest('hex'),
  };
  const refresh = async () => {
    await f.writeJournal();
    Object.assign(f.result.observation, {
      issue: 2724, running: true, activeWork: true, assignmentEventId: assignment.id, successorJobId: successor.job.jobId,
      currentAssignmentConfirmed: true, latestUserEventId: assignment.id,
    });
    delete f.result.observation.followUpPending;
  };
  f.git[`merge-base --is-ancestor ${successor.job.baseSha} HEAD`] = '';
  await refresh();
  const handoff = (expectedGeneration = 206) => recordLocalSessionHandoff({ result: f.result, successor, expectedGeneration }, f.options);
  return { ...f, assignment, activity, receipt, successor, refresh, handoff };
}

test('atomic handoff completes the exact historical task and keeps occupancy constant with a new fenced successor', async (t) => {
  const f = await handoffFixture(t);
  const before = await f.readLedger();
  const result = await f.handoff();
  const after = await f.readLedger();
  assert.equal(after.generation, 207);
  assert.equal(result.completed.state, 'completed');
  assert.equal(result.completed.exitCode, undefined);
  assert.equal(result.completed.fence, 198);
  assert.equal(result.successor.state, 'accepted');
  assert.equal(result.successor.fence, 207);
  assert.equal(result.successor.predecessorJobId, jobId);
  assert.equal(result.successor.sessionId, sessionId);
  assert.equal(result.successor.handoffBoundary.assignmentEventId, f.assignment.id);
  const count = (ledger) => Object.values(ledger.jobs).filter((entry) => activeJobStates.has(entry.state)).length;
  assert.equal(count(after), count(before));
  for (const id of ['retained', 'retired']) assert.deepEqual(after.jobs[id], before.jobs[id]);
  assert.deepEqual(await f.handoff(207), result);
  assert.equal((await f.readLedger()).generation, 207);
});

test('ordinary completion never accepts later task activity that only the separate handoff can account', async (t) => {
  const f = await handoffFixture(t);
  await assert.rejects(() => f.complete(), (error) => error.code === 'INVALID_SESSION_COMPLETION');
  assert.equal((await f.readLedger()).generation, 206);
});

test('atomic handoff rejects mismatched, ambiguous, missing or completed successor evidence without partial writes', async (t) => {
  const cases = [
    ['other session', (f) => { f.successor.sessionId = randomUUID(); }],
    ['duplicate old job', (f) => { f.successor.job.jobId = jobId; }],
    ['same old issue', (f) => { f.successor.job.issue = 2720; }],
    ['duplicate issue', (f) => { f.ledger.jobs.retained.issue = 2724; }],
    ['active secondary PR issue', (f) => {
      f.ledger.jobs.retained.prRecovery = { work: { linkedIssues: [2661, 2724] } };
    }],
    ['stranded secondary PR issue', (f) => {
      Object.assign(f.ledger.jobs.retained, {
        state: 'failed', failureReason: 'kickoff-unverified', strandedSessionId: randomUUID(),
        prRecovery: { work: { linkedIssues: [2661, 2724] } },
      });
    }],
    ['predecessor secondary PR issue is not distinct work', (f) => {
      f.entry.prRecovery = { work: { linkedIssues: [2720, 2724] } };
    }],
    ['existing new identifier', (f) => { f.ledger.jobs[f.successor.job.jobId] = { state: 'failed' }; }],
    ['wrong assignment source', (f) => { f.successor.assignmentSource = `agent-${randomUUID()}`; }],
    ['wrong assignment digest', (f) => { f.successor.assignmentContentSha256 = 'a'.repeat(64); }],
    ['missing assignment', (f) => { f.successor.assignmentEventId = randomUUID(); }],
    ['wrong current issue', (f) => { f.result.observation.issue = 2661; }],
    ['not active', (f) => { f.result.observation.activeWork = false; }],
    ['unmatched activity', (f) => { f.activity.data.interactionId = randomUUID(); }],
    ['missing delivery', (f) => { f.successor.deliveryEventId = randomUUID(); }],
    ['failed delivery receipt', (f) => { f.receipt.data.success = false; }],
    ['unpaired delivery receipt', (f) => { f.receipt.data.toolCallId = 'missing-start'; }],
    ['changed delivery', (f) => { f.successor.deliveryContentSha256 = 'a'.repeat(64); }],
    ['missing old clean/pushed attestation', (f) => { f.events.find((event) => event.id === f.result.taskCompleteEventId).data.summary = 'Done'; }],
    ['completed destination', (f) => { f.events.push({ id: randomUUID(), type: 'session.task_complete', data: { success: true }, timestamp: timestamp(3_000), parentId: f.activity.id }); }],
    ['later root input before selected assignment', (f) => {
      const index = f.events.indexOf(f.assignment);
      const other = { id: randomUUID(), type: 'user.message', timestamp: timestamp(6_000), data: { content: 'Different task' }, parentId: f.events[index - 1].id };
      f.assignment.parentId = other.id;
      f.events.splice(index, 0, other);
    }],
  ];
  for (const [name, change] of cases) await t.test(name, async (subtest) => {
    const f = await handoffFixture(subtest);
    change(f);
    await f.saveLedger();
    const observation = structuredClone(f.result.observation);
    await f.writeJournal();
    Object.assign(f.result.observation, observation, {
      journalSha256: f.result.observation.journalSha256, runtimeHeadEventId: f.events.at(-1).id,
    });
    const before = await readFile(f.ledgerFile, 'utf8');
    await assert.rejects(() => f.handoff(), (error) => error.code === 'INVALID_SESSION_COMPLETION');
    assert.equal(await readFile(f.ledgerFile, 'utf8'), before);
  });
});

test('atomic handoff concurrent CAS permits one winner and never exposes a released intermediate slot', async (t) => {
  const f = await handoffFixture(t);
  const outcomes = await Promise.allSettled([f.handoff(), f.handoff()]);
  assert.equal(outcomes.filter((outcome) => outcome.status === 'fulfilled').length, 1);
  assert.equal(outcomes.find((outcome) => outcome.status === 'rejected').reason.code, 'STALE_LEDGER');
  assert.equal(Object.values((await f.readLedger()).jobs).filter((entry) => activeJobStates.has(entry.state)).length, 2);
});

async function completedHandoffFixture(t) {
  const f = await handoffFixture(t);
  for (const key of ['expectedHost', 'model', 'effort', 'agent']) delete f.successor.job[key];
  const newHead = 'c'.repeat(40);
  const content = `${newHead}\trefs/pull/2725/head; clean delivery verified`;
  const receipt = { id: randomUUID(), type: 'tool.execution_complete', timestamp: timestamp(3_000),
    data: { success: true, toolCallId: 'new-delivery', result: { content } } };
  const task = { id: randomUUID(), type: 'session.task_complete', timestamp: timestamp(2_000),
    data: { success: true, summary: '#2724 delivered. Working tree clean; all commits pushed. Admission marker still pending.' } };
  const end = { id: randomUUID(), type: 'assistant.turn_end', timestamp: timestamp(1_500), data: { turnId: 'new-task' } };
  f.events.push({ id: randomUUID(), type: 'tool.execution_start', timestamp: timestamp(3_500),
    data: { toolCallId: 'new-delivery', toolName: 'powershell' } }, receipt, task, end,
  { id: randomUUID(), type: 'session.shutdown', timestamp: timestamp(1_000), data: { shutdownType: 'routine' } });
  f.events.forEach((event, index) => { if (index) event.parentId = f.events[index - 1].id; });
  f.successor.completion = {
    headSha: newHead, publicationRef: 'refs/pull/2725/head', workingTreeClean: true, allCommitsPushed: true,
    taskCompleteEventId: task.id, turnEndEventId: end.id, deliveryEventId: receipt.id,
    deliveryContentSha256: createHash('sha256').update(content).digest('hex'),
    validationEvidence: { headSha: newHead, passed: true, source: 'Verified successor CI/test results at exact HEAD.' },
  };
  f.git[`merge-base --is-ancestor ${f.successor.job.baseSha} ${newHead}`] = '';
  f.git['ls-remote --exit-code origin refs/pull/2725/head'] = `${newHead}\trefs/pull/2725/head`;
  f.git['rev-parse HEAD'] = newHead;
  const refresh = async () => {
    await f.refresh();
    Object.assign(f.result.observation, { activeWork: false, running: false, followUpPending: false });
  };
  await refresh();
  const completePair = (expectedGeneration = 206) =>
    recordLocalCompletedHandoff({ result: f.result, successor: f.successor, expectedGeneration }, f.options);
  return { ...f, task, end, newReceipt: receipt, refresh, completePair };
}

test('completed pair is one atomic historical reconciliation, not fake active work or a fabricated preexisting fence', async (t) => {
  const f = await completedHandoffFixture(t);
  const before = await f.readLedger();
  const result = await f.completePair();
  const after = await f.readLedger();
  assert.equal(after.generation, 207);
  assert.equal(result.completed.state, 'completed');
  assert.equal(result.successor.state, 'completed');
  assert.equal(result.successor.fence, 207);
  assert.equal(result.successor.predecessorJobId, jobId);
  assert.equal(result.completed.sessionCompletion.taskCompleteEventId, f.result.taskCompleteEventId);
  assert.equal(result.successor.sessionCompletion.taskCompleteEventId, f.task.id);
  assert.equal(result.successor.sessionCompletion.kind, 'app-session-task-retrospective');
  assert.equal(result.successor.sessionCompletion.runtimeReportedAdmission, false);
  assert.equal(result.successor.sessionCompletion.dispatchMetadataRecorded, false);
  for (const key of ['expectedHost', 'model', 'effort', 'agent']) assert.equal(Object.hasOwn(result.successor.job, key), false);
  assert.equal(Object.hasOwn(result.completed, 'exitCode'), false);
  assert.equal(Object.hasOwn(result.successor, 'exitCode'), false);
  assert.equal(Object.values(after.jobs).filter((entry) => activeJobStates.has(entry.state)).length, 1);
  for (const id of ['retained', 'retired']) assert.deepEqual(after.jobs[id], before.jobs[id]);
  assert.deepEqual(await f.completePair(207), result);
  assert.equal((await f.readLedger()).generation, 207);
});

test('active handoff cannot implicitly import a completed destination', async (t) => {
  const f = await completedHandoffFixture(t);
  await assert.rejects(() => f.handoff(), (error) => error.code === 'INVALID_SESSION_COMPLETION');
  assert.equal((await f.readLedger()).generation, 206);
});

test('completed pair rejects live, wrong, missing, failed and resumed successor evidence without partial writes', async (t) => {
  const cases = [
    ['same task ID', (f) => { f.successor.completion.taskCompleteEventId = f.result.taskCompleteEventId; }],
    ['same turn end', (f) => { f.successor.completion.turnEndEventId = f.result.turnEndEventId; }],
    ['wrong reported issue', (f) => { f.task.data.summary = '#27240 complete. Working tree clean; all commits pushed.'; }],
    ['failed new task', (f) => { f.task.data.success = false; }],
    ['embedded new task', (f) => { f.task.agentId = randomUUID(); }],
    ['missing new task', (f) => { f.successor.completion.taskCompleteEventId = randomUUID(); }],
    ['missing new end', (f) => { f.successor.completion.turnEndEventId = randomUUID(); }],
    ['wrong new turn', (f) => { f.end.data.turnId = 'old-turn'; }],
    ['failed new delivery', (f) => { f.newReceipt.data.success = false; }],
    ['old receipt relabelled new', (f) => { f.successor.completion.deliveryEventId = f.successor.deliveryEventId; }],
    ['dirty now', (f) => { f.git['status --porcelain=v1 --untracked-files=all'] = ' M actual-new-work'; }],
    ['unpublished new head', (f) => { f.git['ls-remote --exit-code origin refs/pull/2725/head'] = ''; }],
    ['failed new validation', (f) => { f.successor.completion.validationEvidence.passed = false; }],
    ['invented exit', (f) => { f.successor.completion.exitCode = 0; }],
    ['still running', (f) => { f.result.observation.running = true; }],
    ['claimed active', (f) => { f.result.observation.activeWork = true; }],
    ['queued followup', (f) => { f.result.observation.followUpPending = true; }],
    ['changed current issue', (f) => { f.result.observation.issue = 2727; }],
    ['later user work', (f) => { f.events.push({ id: randomUUID(), parentId: f.events.at(-1).id,
      timestamp: timestamp(500), type: 'user.message', data: { content: 'New issue #2727' } }); }],
    ['unrecognized shutdown', (f) => { f.events.at(-1).data.shutdownType = 'crash'; }],
  ];
  for (const [name, change] of cases) await t.test(name, async (subtest) => {
    const f = await completedHandoffFixture(subtest);
    change(f);
    const observation = structuredClone(f.result.observation);
    await f.writeJournal();
    Object.assign(f.result.observation, observation, {
      journalSha256: f.result.observation.journalSha256, runtimeHeadEventId: f.events.at(-1).id,
    });
    const before = await readFile(f.ledgerFile, 'utf8');
    await assert.rejects(() => f.completePair(), (error) => error.code === 'INVALID_SESSION_COMPLETION');
    assert.equal(await readFile(f.ledgerFile, 'utf8'), before);
  });
});

test('completed pair concurrent writers release only the single retained slot', async (t) => {
  const f = await completedHandoffFixture(t);
  const outcomes = await Promise.allSettled([f.completePair(), f.completePair()]);
  assert.equal(outcomes.filter((outcome) => outcome.status === 'fulfilled').length, 1);
  assert.equal(outcomes.find((outcome) => outcome.status === 'rejected').reason.code, 'STALE_LEDGER');
  assert.equal(Object.values((await f.readLedger()).jobs).filter((entry) => activeJobStates.has(entry.state)).length, 1);
});

test('native shutdown/resume explicitly starts a fresh runtime epoch without manufacturing task success', async (t) => {
  for (const kind of ['valid', 'active-old-host', 'wrong-cwd', 'no-shutdown']) await t.test(kind, async (subtest) => {
    const f = await completedHandoffFixture(subtest);
    const index = f.events.indexOf(f.newReceipt) - 1;
    const boundary = [
      ...(kind === 'no-shutdown' ? [] : [{ type: 'session.shutdown', data: { shutdownType: 'routine' } }]),
      { type: 'session.resume', data: { sessionWasActive: kind === 'active-old-host', alreadyInUse: false,
        context: { cwd: kind === 'wrong-cwd' ? path.join(f.root, 'other') : f.root } } },
      { type: 'assistant.turn_start', data: { turnId: 'new-task', interactionId: f.assignment.data.interactionId } },
    ].map((event) => ({ ...event, id: randomUUID(), timestamp: timestamp(3_700) }));
    f.events.splice(index, 0, ...boundary);
    f.events.forEach((event, position) => { if (position) event.parentId = f.events[position - 1].id; });
    await f.refresh();
    if (kind === 'valid') {
      const recorded = await f.completePair();
      assert.equal(recorded.completed.sessionCompletion.runtimeEpochs[0].interruptedTurnCount, 1);
      assert.equal(recorded.successor.exitCode, undefined);
    } else await assert.rejects(() => f.completePair(), (error) => error.code === 'INVALID_SESSION_COMPLETION');
  });

});

function insertCheckpoint(f, side) {
  const finalId = side === 'old' ? f.result.taskCompleteEventId : f.successor.completion.taskCompleteEventId;
  const index = f.events.findIndex((event) => event.id === finalId);
  const turnId = side === 'old' ? 'turn-1' : 'new-task';
  const checkpoint = { id: randomUUID(), type: 'session.task_complete', timestamp: f.events[index - 1].timestamp,
    data: { success: true, summary: 'Reviewed delivery checkpoint; final accounting has not happened.' } };
  const pairedEnd = { id: randomUUID(), type: 'assistant.turn_end', timestamp: checkpoint.timestamp, data: { turnId } };
  f.events.splice(index, 0, checkpoint, pairedEnd,
    { id: randomUUID(), type: 'assistant.turn_start', timestamp: checkpoint.timestamp, data: { turnId } });
  f.events.forEach((event, position) => { if (position) event.parentId = f.events[position - 1].id; });
  const proof = side === 'old' ? f.result : f.successor.completion;
  proof.priorTaskCompleteEventIds = [checkpoint.id];
  return { checkpoint, pairedEnd, proof };
}

test('explicit successful checkpoints preserve separate paired-turn audit and bind replay', async (t) => {
  const f = await completedHandoffFixture(t);
  const old = insertCheckpoint(f, 'old');
  const next = insertCheckpoint(f, 'new');
  await f.refresh();
  const recorded = await f.completePair();
  for (const [entry, checkpoint] of [[recorded.completed, old], [recorded.successor, next]]) {
    assert.equal(entry.sessionCompletion.checkpoints[0].id, checkpoint.checkpoint.id);
    assert.equal(entry.sessionCompletion.checkpoints[0].turnEndEventId, checkpoint.pairedEnd.id);
    assert.equal(entry.sessionCompletion.checkpoints[0].summarySha256.length, 64);
  }
  assert.deepEqual(await f.completePair(207), recorded);
  old.proof.priorTaskCompleteEventIds = [];
  await assert.rejects(() => f.completePair(207), (error) => error.code === 'INVALID_SESSION_COMPLETION');
});

test('completion checkpoints reject missing, failed, unlisted, cross-side and ambiguous boundaries', async (t) => {
  for (const side of ['old', 'new']) for (const kind of
    ['undeclared', 'failed', 'missing', 'duplicate', 'wrong-side', 'final-reused', 'unpaired', 'unordered']) {
    await t.test(`${side}: ${kind}`, async (subtest) => {
      const f = await completedHandoffFixture(subtest);
      const { checkpoint, pairedEnd, proof } = insertCheckpoint(f, side);
      if (kind === 'undeclared') delete proof.priorTaskCompleteEventIds;
      if (kind === 'failed') checkpoint.data.success = false;
      if (kind === 'missing') proof.priorTaskCompleteEventIds = [randomUUID()];
      if (kind === 'duplicate') proof.priorTaskCompleteEventIds.push(checkpoint.id);
      if (kind === 'wrong-side') {
        delete proof.priorTaskCompleteEventIds;
        (side === 'old' ? f.successor.completion : f.result).priorTaskCompleteEventIds = [checkpoint.id];
      }
      if (kind === 'final-reused') proof.priorTaskCompleteEventIds = [proof.taskCompleteEventId];
      if (kind === 'unpaired') pairedEnd.data.turnId = 'another-turn';
      if (kind === 'unordered') proof.priorTaskCompleteEventIds = [randomUUID(), checkpoint.id];
      await f.refresh();
      await assert.rejects(() => f.completePair(), (error) => error.code === 'INVALID_SESSION_COMPLETION');
      assert.equal((await f.readLedger()).generation, 206);
    });
  }
});

test('successor delivery cannot reuse old, consumed or pre-assignment tool lifecycles', async (t) => {
  for (const kind of ['duplicate-start', 'old-start', 'pre-activity-start', 'consumed-receipt', 'missing-start']) {
    await t.test(kind, async (subtest) => {
      const f = await completedHandoffFixture(subtest);
      const index = f.events.indexOf(f.newReceipt) - 1;
      const start = f.events[index];
      if (kind === 'duplicate-start') start.data.toolCallId = 'recorded-delivery';
      if (kind === 'old-start') {
        f.events.splice(index, 1);
        start.timestamp = f.events[1].timestamp;
        f.events.splice(2, 0, start);
      }
      if (kind === 'pre-activity-start') {
        f.events.splice(index, 1);
        start.timestamp = f.assignment.timestamp;
        f.events.splice(f.events.indexOf(f.activity), 0, start);
      }
      if (kind === 'consumed-receipt') {
        f.events.splice(index + 1, 0, { ...structuredClone(f.newReceipt), id: randomUUID() });
      }
      if (kind === 'missing-start') f.events.splice(index, 1);
      f.events.forEach((event, position) => { if (position) event.parentId = f.events[position - 1].id; });
      await f.refresh();
      await assert.rejects(() => f.completePair(), (error) => error.code === 'INVALID_SESSION_COMPLETION');
      assert.equal((await f.readLedger()).generation, 206);
    });
  }
});

test('retrospective work identity never accepts guessed dispatch metadata', async (t) => {
  for (const key of ['model', 'effort', 'agent', 'expectedHost']) await t.test(key, async (subtest) => {
    const f = await completedHandoffFixture(subtest);
    f.successor.job[key] = 'guessed';
    await assert.rejects(() => f.completePair(), (error) => error.code === 'INVALID_SESSION_COMPLETION');
    assert.equal((await f.readLedger()).generation, 206);
  });
});
