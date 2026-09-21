import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { promisify } from 'node:util';
import test from 'node:test';
import { policyPaths, resolveAutomationHost, verifyAutomationCheckout } from '../ralph-automation.mjs';

const exec = promisify(execFile);
const config = JSON.parse(await readFile('.copilot/skills/ralph-loop/hosts.json', 'utf8'));
const workflow = config.hosts['macos-mobile'].workflowId;
const runtime = {
  host: 'macos-mobile', workflowId: workflow, appHostId: 'local', projectId: 'fixture-project',
  worktreeRoot: '/test/worktrees', cacheDirectory: '/test/cache', verified: true,
};
const hostInput = {
  host: 'macos-mobile', workflow, runtime, platform: 'darwin', cwd: '/test/worktrees/round-one',
};

test('shared host mapping preserves mobile ownership, capacity, hold and explicit model override', () => {
  const host = resolveAutomationHost(config, hostInput);
  assert.equal(host.scope, 'mobile');
  assert.equal(host.maxLocalSessions, 5);
  assert.equal(host.maxLocalXcodeJobs, 1);
  assert.deepEqual(host.mergeHeldPrs, [2603]);
  assert.deepEqual(host.dallas, { model: 'gpt-6-astra', reasoningEffort: 'xhigh' });
  assert.equal(host.kickoffClauses, 'macos-kickoff.md');
  assert.equal(host.admission, 'native-local');
  assert.equal(config.hosts['windows-general'].newRemoteMobileDispatch, false);
});

test('unverified Windows instance, wrong workflow/platform and main-checkout paths fail closed', () => {
  for (const override of [
    { host: 'windows-general', platform: 'win32' },
    { workflow: 'wrong-workflow' }, { platform: 'win32' },
    { cwd: '/test/main' }, { cwd: '/test/worktrees' }, { cwd: '/test/worktrees-other/round' },
    { runtime: { ...runtime, verified: false } },
    { runtime: { ...runtime, host: 'windows-general' } },
    { runtime: { ...runtime, cacheDirectory: 'relative/path' } },
  ]) assert.throws(() => resolveAutomationHost(config, { ...hostInput, ...override }));
});

test('private host config cannot widen repository-controlled limits or review scope', () => {
  const host = resolveAutomationHost(config, {
    ...hostInput, runtime: { ...runtime, maxLocalSessions: 99, scope: 'general', mergeHeldPrs: [] },
  });
  assert.equal(host.maxLocalSessions, 5);
  assert.equal(host.scope, 'mobile');
  assert.deepEqual(host.mergeHeldPrs, [2603]);
});

async function gitFixture(t) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'ralph-policy-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const cwd = path.join(root, 'round');
  const remote = path.join(root, 'origin.git');
  const git = async (directory, args) => (await exec('git', args, { cwd: directory })).stdout;
  await git(root, ['init', '--bare', '-q', remote]);
  await git(root, ['init', '-q', '-b', 'development', cwd]);
  await git(cwd, ['config', 'user.name', 'Policy fixture']);
  await git(cwd, ['config', 'user.email', 'fixture@example.invalid']);
  await git(cwd, ['remote', 'add', 'origin', remote]);
  const file = '.copilot/skills/ralph-loop/automation.md';
  await mkdir(path.dirname(path.join(cwd, file)), { recursive: true });
  await writeFile(path.join(cwd, file), 'approved policy\n');
  await git(cwd, ['add', file]);
  await git(cwd, ['commit', '-q', '-m', 'Policy fixture']);
  await git(cwd, ['push', '-q', '-u', 'origin', 'development']);
  const approvedPolicy = (await git(cwd, ['rev-parse', 'HEAD'])).trim();
  const verifierGit = (directory, args) => args.join(' ') === 'remote get-url origin'
    ? Promise.resolve('https://github.com/OlyForge3D/PrintFarmer.git\n') : git(directory, args);
  return { root, cwd, git, file, options: { cwd, approvedPolicy, git: verifierGit } };
}

test('real Git guard accepts approved policy and unrelated code, rejects modified policy and untracked policy', async (t) => {
  const f = await gitFixture(t);
  await verifyAutomationCheckout(f.options);
  await writeFile(path.join(f.cwd, 'application.txt'), 'unrelated\n');
  await f.git(f.cwd, ['add', 'application.txt']);
  await f.git(f.cwd, ['commit', '-q', '-m', 'Unrelated fixture']);
  await f.git(f.cwd, ['push', '-q']);
  await verifyAutomationCheckout(f.options);
  await writeFile(path.join(f.cwd, f.file), 'unreviewed edits\n');
  await assert.rejects(() => verifyAutomationCheckout(f.options));
  await writeFile(path.join(f.cwd, f.file), 'approved policy\n');
  await writeFile(path.join(f.cwd, '.copilot/skills/ralph-loop/unapproved.md'), 'new instructions\n');
  await assert.rejects(() => verifyAutomationCheckout(f.options), /Untracked policy/);
});

test('real Git guard rejects stale deployment pin, invalid SHA and wrong origin', async (t) => {
  const f = await gitFixture(t);
  await writeFile(path.join(f.cwd, f.file), 'new policy\n');
  await f.git(f.cwd, ['add', f.file]);
  await f.git(f.cwd, ['commit', '-q', '-m', 'Policy changed fixture']);
  await f.git(f.cwd, ['push', '-q']);
  await assert.rejects(() => verifyAutomationCheckout(f.options));
  await assert.rejects(() => verifyAutomationCheckout({ ...f.options, approvedPolicy: 'development' }), /full policy commit/);
  await assert.rejects(() => verifyAutomationCheckout({ ...f.options, git: () => 'https://example.invalid/fork.git' }), /origin must/);
});

test('common policy retains actual handoff, host ownership, dependency and merge protections', async () => {
  const [common, bootstrap, kickoff, skill] = await Promise.all([
    readFile('.copilot/skills/ralph-loop/automation.md', 'utf8'),
    readFile('.copilot/skills/ralph-loop/bootstrap.md', 'utf8'),
    readFile('.copilot/skills/ralph-loop/macos-kickoff.md', 'utf8'),
    readFile('.copilot/skills/ralph-loop/SKILL.md', 'utf8'),
  ]);
  for (const required of [
    'one round and exit', 'all open PRs, including drafts', 'A blocked parent',
    'ralph-pr-handoff', 'exact starting head', 'actual session ID',
    'A created session is not a started session', 'existing shared authority',
    'do not lie by passing', 'match-head-commit', 'APPROVED',
    're-fetch the native dependency edges', 'no new distributed lock service',
    'Unknown remote owner', 'one resend', 'never archive/delete',
  ]) assert.ok(common.toLowerCase().includes(required.toLowerCase()), required);
  assert.match(bootstrap, /Windows is not live-verified/);
  assert.match(bootstrap, /squad watch --execute.*not this entrypoint/s);
  assert.match(skill, /automation\.md/);
  assert.match(kickoff, /BEFORE YOU OPEN YOUR PULL REQUEST, SYNC TO THE CURRENT BASE/);
  assert.match(kickoff, /PUSH YOUR BRANCH FIRST/);
  assert.ok(policyPaths.includes('scripts/ci/ralph-pr-recovery.mjs'));
});
