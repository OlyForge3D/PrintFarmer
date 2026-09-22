import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { createHash } from 'node:crypto';
import { chmod, link, lstat, mkdir, mkdtemp, readFile, readdir, realpath, rm, symlink, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { promisify } from 'node:util';
import test from 'node:test';
import { confirmPolicy, help, parseArgs, runCommand, setup, validatePolicy } from '../../setup-ralph-macos.mjs';
import { resolveAutomationHost } from '../ralph-automation.mjs';

const exec = promisify(execFile);
const policy = JSON.parse(await readFile('.copilot/skills/ralph-loop/hosts.json', 'utf8'));
const bootstrap = await readFile('.copilot/skills/ralph-loop/bootstrap.md', 'utf8');
const nativeRoles = await readFile('.copilot/skills/ralph-loop/native-roles.md', 'utf8');
const squadAgent = await readFile('.github/agents/squad.agent.md', 'utf8');
const workflow = 'aaaaaaaa-1111-4222-8333-444444444444';
const otherWorkflow = '11111111-2222-4333-8444-555555555555';

async function fixture(t) {
  const root = await mkdtemp(path.join(await realpath(os.tmpdir()), 'ralph setup fixture '));
  t.after(() => rm(root, { recursive: true, force: true }));
  const repo = path.join(root, "repo with spaces and 'quote'");
  await mkdir(path.join(repo, '.copilot/skills/ralph-loop'), { recursive: true });
  await mkdir(path.join(repo, 'scripts/ci'), { recursive: true });
  await mkdir(path.join(repo, '.github/agents'), { recursive: true });
  await mkdir(path.join(repo, '.squad'), { recursive: true });
  for (const [file, content] of [
    ['.copilot/skills/ralph-loop/hosts.json', JSON.stringify(policy)],
    ['.copilot/skills/ralph-loop/bootstrap.md', bootstrap],
    ['.copilot/skills/ralph-loop/native-roles.md', nativeRoles],
    ['.github/agents/squad.agent.md', squadAgent],
    ['.github/copilot-instructions.md', 'approved\n'], ['.squad/config.json', '{}\n'],
    ['scripts/ci/ralph-automation.mjs', '// approved fixture\n'],
    ['scripts/ci/ralph-mailbox.mjs', '// approved fixture\n'],
    ['scripts/ci/ralph-native-runtime.mjs', '// approved fixture\n'],
    ['scripts/ci/ralph-native-dispatch.mjs', '// approved fixture\n'],
    ['scripts/ci/resolve-ios-simulator.sh', '# approved fixture\n'],
    ['scripts/common-utils.sh', '# approved fixture\n'],
  ]) await writeFile(path.join(repo, file), content);
  const git = async (args) => (await exec('git', args, { cwd: repo })).stdout.trim();
  await git(['init', '-q', '-b', 'development']);
  await git(['config', 'user.name', 'Setup fixture']);
  await git(['config', 'user.email', 'fixture@example.invalid']);
  await git(['remote', 'add', 'origin', 'https://github.com/OlyForge3D/PrintFarmer.git']);
  await git(['add', '.']);
  await git(['commit', '-qm', 'Approved fixture']);
  const approved = await git(['rev-parse', 'HEAD']);
  const options = {
    repo, 'host-config': path.join(root, "private 'config", 'host.json'),
    'project-id': 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',
    'workflow-id': workflow, 'app-host-id': 'fixture-host',
    'worktree-root': path.join(root, 'app worktrees'),
    'cache-dir': path.join(root, 'cold cache'),
    'github-login': 'fixture-user', 'approved-policy': approved,
  };
  const calls = [];
  const state = {
    origin: undefined, login: 'fixture-user', repository: 'OlyForge3D/PrintFarmer', write: true,
    comparison: { status: 'identical', merge_base_commit: { sha: approved }, files: [] },
    development: approved, truncatedTree: false, fail: () => false,
    control: {
      id: 123, full_name: 'fixture/private-control', private: true, visibility: 'private',
      archived: false, disabled: false, permissions: { push: true },
    },
  };
  const command = async (tool, args, settings = {}) => {
    calls.push({ tool, args, settings });
    if (state.fail(tool, args)) throw new Error('fixture tool/auth failure TOKEN_SHOULD_NOT_LEAK');
    if (tool === 'git') {
      if (args.join(' ') === 'remote get-url origin' && state.origin) return state.origin;
      return (await exec('git', args, { cwd: settings.cwd ?? repo })).stdout.trim();
    }
    if (tool === 'gh' && args[0] === 'api') {
      const endpoint = args.at(-1);
      if (endpoint === 'user') return JSON.stringify({ login: state.login });
      if (endpoint === 'repos/OlyForge3D/PrintFarmer') return JSON.stringify({ full_name: state.repository, permissions: { push: state.write } });
      if (endpoint === 'repos/fixture/private-control') return JSON.stringify(state.control);
      if (endpoint.endsWith('/commits/development')) return JSON.stringify({ sha: state.development });
      if (endpoint.includes('/compare/')) return JSON.stringify(state.comparison);
      if (endpoint.includes('/git/commits/')) return JSON.stringify({
        sha: state.development, tree: { sha: await git(['rev-parse', `${state.development}^{tree}`]) },
      });
      if (endpoint.includes('/git/trees/')) {
        const entries = (await git(['ls-tree', '-r', '-z', state.development])).split('\0').filter(Boolean).map((entry) => {
          const [, mode, type, sha, name] = entry.match(/^(\d+) (\w+) (\w+)\t([\s\S]+)$/);
          return { mode, type, sha, path: name };
        });
        for (const changed of state.comparison.files ?? []) {
          if (changed.filename.startsWith('.copilot/') || changed.previous_filename?.startsWith('scripts/ci/ralph-')) {
            entries.push({ mode: '100644', type: 'blob', sha: 'b'.repeat(40), path: '.copilot/skills/ralph-loop/new-policy.md' });
          }
        }
        return JSON.stringify({ sha: await git(['rev-parse', `${state.development}^{tree}`]), truncated: state.truncatedTree, tree: entries });
      }
      throw new Error(`Unexpected endpoint: ${endpoint}`);
    }
    if (tool === 'xcodebuild') return 'Xcode 26.5\nBuild version fixture';
    if (tool === 'bash') return 'fixture-simulator-udid';
    return 'fixture version';
  };
  return { root, repo, git, approved, options, calls, state, command,
    run: (overrides = {}, dependencies = {}) => setup({ ...options, ...overrides }, {
      platform: 'darwin', command, confirm: async () => true, ...dependencies,
    }) };
}

async function nativeOptions(f, role = 'consumer', windows = false) {
  const registry = {
    version: 1, authorityId: 'primary', epoch: 1, writers: ['fixture-user'],
    workers: [
      { workerId: 'mini', host: 'macos-mobile', capabilities: ['general', 'ios'] },
      { workerId: 'windows', host: 'windows-general', capabilities: ['general'] },
    ],
  };
  const registryFile = path.join(f.root, 'registry.json');
  await writeFile(registryFile, JSON.stringify(registry), { mode: 0o600 });
  return { role, 'worker-id': windows ? 'windows' : 'mini', 'worker-registry': registryFile,
    'control-repo': 'fixture/private-control', 'mailbox-ref': 'heads/main' };
}

test('native role dry-run probes exact private repo without queue/native writes', async (t) => {
  const f = await fixture(t);
  const options = await nativeOptions(f, 'coordinator');
  const result = await f.run(options);
  assert.equal(result.status, 'approval-required');
  assert.equal(result.policyReview.control.repositoryId, 123);
  assert.equal(result.policyReview.nativeRole, 'coordinator');
  assert.equal(result.policyReview.control.sharedWriterTrustAccepted, true);
  assert.equal(f.calls.some((call) => call.tool === 'gh' && call.args.some((value) => ['POST', 'PUT', 'PATCH'].includes(value))), false);
  await assert.rejects(readFile(f.options['host-config']), /ENOENT/);
});

function assertNativePrompt(prompt, role) {
  assert.match(prompt, /Inventory scope is Ralph-owned lineage across rounds, NOT all project sessions/);
  assert.match(prompt, /ownershipScope:"ralph-owned-v1" and lineageChecked:true/);
  assert.match(prompt, /Missing creation ACKs, missing live mapped workers, resumed terminal workers/);
  assert.match(prompt, /Unrelated maintainer and other-automation sessions consume no Ralph/);
  assert.match(prompt, /Every retained mapping, including terminal work, needs an explicit inventory/);
  assert.match(prompt, /retirementObservation contract: current verified cessation/);
  assert.match(prompt, /Only a successful\nruntime response with acquisitionAbandoned:true/);
  assert.match(prompt, /NEW round\/event IDs in this invocation/);
  assert.match(prompt, /THREE acquisition attempts total \(initial plus\ntwo retries\)/);
  assert.match(prompt, /only after runtime-proven abandonment of each failed attempt/);
  assert.match(prompt, /Keep the old journal\/intents immutable/);
  assert.match(prompt, /unchanged head, timeout, failed reconciliation, published acquisition or\nunproven outcome must STOP/);
  assert.match(prompt, /No polling, sleeps or schedule changes/);
  assert.match(prompt, /complete exactly one role round/);
  if (role === 'coordinator') {
    assert.match(prompt, /MUST complete missing-label triage while\nholding its round gate/);
    assert.match(prompt, /Read each candidate's current body/);
    assert.match(prompt, /Add a justified missing type:\*, priority:p0-p3/);
    assert.match(prompt, /Preserve existing valid classifications and ownership, go:needs-research,\ngo:no, explicit holds and status:needs-analysis/);
    assert.match(prompt, /Never assign jpapiez, overwrite\nconflicting classifications, guess labels/);
    assert.match(prompt, /evidence is ambiguous,\nreport the specific unresolved classification/);
    assert.match(prompt, /Re-read GitHub after label changes/);
    assert.match(prompt, /actual readback as fresh evidence for same-round research-plan\/reserve/);
    assert.match(prompt, /metadata triage only, not unaccounted research or implementation/);
    assert.match(prompt, /type:"research-plan" is a read-only, non-mutating disposition check/);
    assert.match(prompt, /type:"reserve", purpose:"research"/);
    assert.match(prompt, /then publish it with assignmentId\/generation\/taskDigest/);
  } else {
    assert.doesNotMatch(prompt, /missing-label triage|Add a justified missing|research-reservation candidate|type:"reserve", purpose:"research"/);
  }
}

test('native packages stage coordinator and both consumers with role-specific triage and bounded recovery', async (t) => {
  for (const [role, windows] of [['coordinator', false], ['consumer', false], ['consumer', true]]) {
    const f = await fixture(t);
    const options = await nativeOptions(f, role, windows);
    const result = await f.run({ ...options, apply: true }, { platform: windows ? 'win32' : 'darwin' });
    assert.equal(result.status, 'staged-unverified');
    const host = JSON.parse(await readFile(f.options['host-config'], 'utf8'));
    assert.equal(host.role, role);
    assert.equal(host.host, windows ? 'windows-general' : 'macos-mobile');
    assert.equal(host.verified, false);
    assert.equal(host.migrationAttested, false);
    assert.equal(host.control.genesisSha, undefined);
    assert.equal(host.control.repositoryId, 123);
    const settings = JSON.parse(await readFile(path.join(path.dirname(f.options['host-config']), 'workflow-settings.json'), 'utf8'));
    assert.equal(settings.enabled, false);
    assert.match(settings.prompt, /native-roles\.md/);
    assert.match(settings.prompt, /Do NOT follow the legacy/);
    assert.match(settings.prompt, /owner-configured deployment assertions/);
    assert.match(settings.prompt, /Acquire a fresh atomic begin-round token/);
    assert.match(settings.prompt, /finite durable capacity credits, NOT online presence/);
    assert.match(settings.prompt, /does not require sub-minute consumer timing/);
    assert.match(settings.prompt, /scope and classificationComplete/);
    assert.match(settings.prompt, /new Date\(\)\.toISOString\(\)/);
    assert.match(settings.prompt, /registered agent is Squad in RALPH-ASSIGNED-WORKER-V1/);
    assert.match(settings.prompt, /record-creation/);
    assert.match(settings.prompt, /startup-check/);
    assert.match(settings.prompt, /prestart-proof/);
    assertNativePrompt(settings.prompt, role);
    assert.equal(await readFile(path.join(path.dirname(f.options['host-config']), 'workflow-prompt.txt'), 'utf8'), `${settings.prompt}\n`);
    assert.equal(host.executionTrust, 'local-owner-v1');
    assert.match(settings.name, new RegExp(role));
    const handoff = await readFile(path.join(path.dirname(f.options['host-config']), 'app-native-handoff.txt'), 'utf8');
    assert.match(handoff, /attestations are prerequisites for\ninitialization, not permission to write the queue/);
    assert.match(handoff, /automationWorkflowIds in EACH mini package/);
    assert.ok(result.activationBlockers.some((blocker) => blocker.includes('automationWorkflowIds') && blocker.includes('both mini')));
    if (windows) assert.equal(f.calls.some((call) => ['xcode-select', 'xcodebuild', 'xcrun', 'bash', 'python3'].includes(call.tool)), false);
  }
});

test('native prompt generation rejects a pin without the owned-lineage contract before writing', async (t) => {
  const f = await fixture(t);
  await writeFile(path.join(f.repo, '.copilot/skills/ralph-loop/native-roles.md'), 'NATIVE-MAILBOX-ROLE-V2\nLegacy project-wide inventory.\n');
  await f.git(['add', '.']);
  await f.git(['commit', '-qm', 'Legacy inventory contract']);
  f.state.development = await f.git(['rev-parse', 'HEAD']);
  f.state.comparison = { status: 'identical', merge_base_commit: { sha: f.state.development }, files: [] };
  const options = await nativeOptions(f);
  await assert.rejects(f.run({ ...options, 'approved-policy': f.state.development, apply: true }), /lacks Ralph-owned lineage inventory/);
  await assert.rejects(readFile(f.options['host-config']), /ENOENT/);
});

test('native setup rejects public/wrong/unwritable control repo and approval-time changes', async (t) => {
  for (const change of [{ private: false }, { visibility: 'public' }, { full_name: 'other/private' }, { permissions: { push: false } }, { id: undefined }]) {
    const f = await fixture(t);
    const options = await nativeOptions(f);
    Object.assign(f.state.control, change);
    await assert.rejects(f.run(options), /PRIVATE/);
  }
  const f = await fixture(t);
  const options = await nativeOptions(f);
  await assert.rejects(f.run({ ...options, apply: true }, { confirm: async () => {
    f.state.control.id = 456; return true;
  } }), /changed during approval/);
  await assert.rejects(readFile(f.options['host-config']), /ENOENT/);
});

test('consented native renewal preserves IDs, pinned authority and original journal without overwriting staged package', async (t) => {
  const f = await fixture(t);
  const roleOptions = await nativeOptions(f, 'coordinator');
  await f.run({ ...roleOptions, apply: true });
  const oldPath = f.options['host-config'], oldDirectory = path.dirname(oldPath);
  const old = JSON.parse(await readFile(oldPath, 'utf8'));
  delete old.executionTrust;
  old.control.genesisSha = 'c'.repeat(40);
  old.verified = true;
  old.migrationAttested = true;
  old.automationWorkflowIds.push(otherWorkflow);
  await writeFile(oldPath, JSON.stringify(old), { mode: 0o600 });
  await mkdir(old.stateDirectory, { mode: 0o700 });
  const history = '{"claims":["retain"],"rounds":["do-not-reset"]}';
  await writeFile(path.join(old.stateDirectory, 'journal.json'), history, { mode: 0o600 });
  const oldFiles = new Map(await Promise.all((await readdir(oldDirectory))
    .filter((file) => file !== 'native-state').map(async (file) => [file, await readFile(path.join(oldDirectory, file), 'utf8')])));
  const updatedPath = path.join(f.root, 'renewed-package', 'host.json');
  const renew = { ...roleOptions, apply: true, 'renew-policy': true,
    'previous-approval': path.join(oldDirectory, 'policy-approval.json'),
    'previous-host-config': oldPath, 'host-config': updatedPath, 'cache-dir': path.join(f.root, 'new-cache') };
  let approvals = 0;
  await f.run(renew, { confirm: async () => { approvals++; return true; } });
  assert.equal(approvals, 1);
  const updated = JSON.parse(await readFile(updatedPath, 'utf8'));
  assert.equal(updated.verified, false);
  assert.equal(updated.executionTrust, 'local-owner-v1');
  const settings = JSON.parse(await readFile(path.join(path.dirname(updatedPath), 'workflow-settings.json'), 'utf8'));
  assert.deepEqual(Object.keys(settings).sort(), ['enabled', 'prompt', 'workflow_id']);
  assert.equal(settings.workflow_id, old.workflowId);
  assert.equal(settings.enabled, false);
  assertNativePrompt(settings.prompt, 'coordinator');
  for (const key of ['control', 'stateDirectory', 'automationWorkflowIds', 'migrationAttested',
    'projectId', 'workflowId', 'appHostId', 'worktreeRoot', 'workerId', 'role']) assert.deepEqual(updated[key], old[key], key);
  assert.equal(await readFile(path.join(old.stateDirectory, 'journal.json'), 'utf8'), history);
  for (const [file, content] of oldFiles) assert.equal(await readFile(path.join(oldDirectory, file), 'utf8'), content);
  const another = { ...renew, 'host-config': path.join(f.root, 'another-renewal', 'host.json') };
  await assert.rejects(f.run({ ...another, 'workflow-id': otherWorkflow }), /cannot change workflowId/);
  await assert.rejects(f.run({ ...another, 'mailbox-ref': 'heads/another' }), /cannot change control/);
  await assert.rejects(f.run({ ...another, 'previous-host-config': undefined }), /requires --previous-host-config/);
});

test('consumer renewal retains bounded recovery without coordinator label or research triage', async (t) => {
  for (const windows of [false, true]) {
    const f = await fixture(t);
    const options = await nativeOptions(f, 'consumer', windows);
    const dependencies = { platform: windows ? 'win32' : 'darwin' };
    await f.run({ ...options, apply: true }, dependencies);
    const oldPath = f.options['host-config'];
    const oldPrompt = await readFile(path.join(path.dirname(oldPath), 'workflow-prompt.txt'), 'utf8');
    const newPath = path.join(f.root, 'renewed-consumer', 'host.json');
    await f.run({ ...options, apply: true, 'renew-policy': true,
      'previous-approval': path.join(path.dirname(oldPath), 'policy-approval.json'),
      'previous-host-config': oldPath, 'host-config': newPath,
      'cache-dir': path.join(f.root, 'renewed-cache') }, dependencies);
    const settings = JSON.parse(await readFile(path.join(path.dirname(newPath), 'workflow-settings.json'), 'utf8'));
    assertNativePrompt(settings.prompt, 'consumer');
    assert.equal(settings.workflow_id, workflow);
    assert.equal(settings.enabled, false);
    assert.equal(await readFile(path.join(path.dirname(oldPath), 'workflow-prompt.txt'), 'utf8'), oldPrompt);
  }
});

test('legacy approval scope is a renewal baseline, not approval for the newly controlled Squad entrypoint', async (t) => {
  const f = await fixture(t);
  const options = await nativeOptions(f);
  let review;
  await f.run({ ...options, apply: true }, { confirm: async (value) => { review = value; return true; } });
  const approvalPath = path.join(path.dirname(f.options['host-config']), 'policy-approval.json');
  const legacy = JSON.parse(await readFile(approvalPath, 'utf8'));
  legacy.controlledPaths = legacy.controlledPaths.filter((entry) =>
    !['.github/agents/squad.agent.md', '.squad/agents/*/charter.md', '.squad/issue-lifecycle.md'].includes(entry));
  legacy.controlledContentSha256 = createHash('sha256').update(JSON.stringify(
    review.controlledFiles.filter((file) => file.path !== '.github/agents/squad.agent.md'),
  )).digest('hex');
  await writeFile(approvalPath, JSON.stringify(legacy), { mode: 0o600 });
  await assert.rejects(f.run({ ...options, apply: true }), /interactive policy renewal/);
  const newHost = path.join(f.root, 'expanded-scope', 'host.json');
  let approvals = 0;
  await f.run({ ...options, apply: true, 'renew-policy': true,
    'previous-approval': approvalPath, 'previous-host-config': f.options['host-config'],
    'host-config': newHost, 'cache-dir': path.join(f.root, 'expanded-cache') }, {
    confirm: async (value) => {
      approvals++;
      assert.deepEqual(value.newlyControlledPaths, ['.github/agents/squad.agent.md', '.squad/agents/*/charter.md', '.squad/issue-lifecycle.md']);
      return true;
    },
  });
  assert.equal(approvals, 1);
  assert.deepEqual(JSON.parse(await readFile(approvalPath, 'utf8')), legacy);
});

test('role registries cannot multiply quotas, mismatch platform or leak private fields', async (t) => {
  const f = await fixture(t);
  const options = await nativeOptions(f);
  const registry = JSON.parse(await readFile(options['worker-registry'], 'utf8'));
  registry.workers[0].privatePath = '/home/private';
  await writeFile(options['worker-registry'], JSON.stringify(registry));
  await assert.rejects(f.run(options), /paths or secrets/);
  delete registry.workers[0].privatePath;
  registry.workers[0].capabilities = [123];
  await writeFile(options['worker-registry'], JSON.stringify(registry));
  await assert.rejects(f.run(options), /Invalid worker/);
  registry.workers[0].capabilities = ['general', 'ios'];
  registry.writers = ['fixture-user', 'FIXTURE-USER'];
  await writeFile(options['worker-registry'], JSON.stringify(registry));
  await assert.rejects(f.run(options), /Invalid private worker/);
  registry.writers = ['fixture-user'];
  await writeFile(options['worker-registry'], JSON.stringify(registry));
  await assert.rejects(f.run({ ...options, 'worker-id': 'windows' }), /platform/);
  await assert.rejects(f.run({ ...options, role: 'coordinator' }, { platform: 'win32' }), /macOS/);
});

test('help and strict arguments reject missing/invalid bindings, two pins, traversal and unknown options', () => {
  assert.match(help, /Default: read-only/);
  assert.deepEqual(parseArgs(['--help']), { help: true });
  const args = [
    '--repo', '/fixture/repo', '--host-config', '/fixture/private/host.json',
    '--project-id', otherWorkflow, '--workflow-id', workflow, '--app-host-id', 'local',
    '--worktree-root', '/fixture/worktrees', '--cache-dir', '/fixture/cache',
    '--github-login', 'fixture-user', '--approved-policy', 'a'.repeat(40),
  ];
  assert.equal(parseArgs(args).apply, undefined);
  for (const extra of [['--apply', '--dry-run'], ['--verified'], ['--preflight-policy', 'b'.repeat(40)], ['--repo', '/another']]) {
    assert.throws(() => parseArgs([...args, ...extra]));
  }
  for (const [flag, value] of [
    ['--project-id', 'source-project'], ['--workflow-id', ''], ['--app-host-id', 'bad\nhost'],
    ['--approved-policy', 'development'], ['--host-config', '/foo/../bar/host.json'],
    ['--repo', 'relative'], ['--repo', '/'], ['--repo', '/foo/./bar'],
    ['--repo', '/foo//bar'], ['--cache-dir', '/foo/\nbar'],
  ]) {
    const changed = [...args];
    changed[changed.indexOf(flag) + 1] = value;
    assert.throws(() => parseArgs(changed), flag);
  }
  assert.equal(parseArgs(args.slice(0, -2))['approved-policy'], undefined);
  assert.throws(() => parseArgs([...args, '--renew-policy']), /Renewal requires both/);
});

test('dry-run uses only read-only probes, supports spaces, and does not create config/cache/worktrees or fetch', async (t) => {
  const f = await fixture(t);
  const before = await readdir(f.root);
  const result = await f.run();
  assert.equal(result.status, 'approval-required');
  assert.equal(result.localPrerequisitesReady, false);
  assert.equal(result.nativeBindingsAttested, false);
  assert.deepEqual(await readdir(f.root), before);
  assert.equal(await f.git(['status', '--porcelain']), '');
  assert.equal(f.calls.some(({ tool, args }) => tool === 'git' && ['fetch', 'clone', 'checkout', 'reset'].includes(args[0])), false);
  assert.equal(f.calls.filter(({ tool }) => tool === 'bash').length, 0);
  assert.equal(f.calls.some(({ args }) => args.includes('preflight')), false);
});

test('apply is repeatable, writes only private 0600 unverified artifacts and preserves exact bootstrap pin', async (t) => {
  const f = await fixture(t);
  const result = await f.run({ apply: true });
  assert.equal(result.status, 'staged-unverified');
  const config = JSON.parse(await readFile(f.options['host-config'], 'utf8'));
  assert.equal(config.verified, false);
  assert.equal(config.workflowId, workflow);
  assert.throws(() => resolveAutomationHost(policy, {
    host: 'macos-mobile', workflow, runtime: config, platform: 'darwin',
    cwd: path.join(config.worktreeRoot, 'round'),
  }), /verified private host/);
  const directory = path.dirname(f.options['host-config']);
  const settings = JSON.parse(await readFile(path.join(directory, 'workflow-settings.json'), 'utf8'));
  assert.equal(settings.enabled, false);
  assert.equal(settings.host_id, 'fixture-host');
  assert.equal(settings.project_id, f.options['project-id']);
  assert.equal(settings.model, 'gpt-5.6-luna');
  assert.equal(settings.reasoning_effort, 'medium');
  assert.equal(settings.cron_expression, '40 * * * *');
  assert.ok(settings.prompt.includes(`POLICY_COMMIT=${f.approved}`));
  assert.ok(settings.prompt.includes(`--approved-policy ${f.approved}`));
  assert.ok(settings.prompt.includes("'\\''config/host.json'"));
  assert.doesNotMatch(settings.prompt, /THREE acquisition attempts|missing-label triage|research-reservation candidate/);
  const before = await Promise.all(result.files.map(async (file) => [await readFile(file, 'utf8'), (await lstat(file)).mtimeMs]));
  await f.run({ apply: true });
  const after = await Promise.all(result.files.map(async (file) => [await readFile(file, 'utf8'), (await lstat(file)).mtimeMs]));
  assert.deepEqual(after, before);
  for (const file of result.files) assert.equal((await lstat(file)).mode & 0o777, 0o600);
  const handoff = await readFile(path.join(directory, 'app-native-handoff.txt'), 'utf8');
  for (const expected of ['list_projects', 'list_workflows', 'get_session', 'verified:false to verified:true',
    'Idle, absent sessions', 'Windows-owned', 'Do not use run_workflow', 'dispatchAuthorized:false',
    'Reaper remains disabled', 'No launchd', 'quoting \'scripts/ci/ralph-*.mjs\'']) assert.ok(handoff.includes(expected), expected);
});

test('new destination identity needs no shared policy edit and stays unverified', async (t) => {
  const f = await fixture(t);
  const result = await f.run({ apply: true, 'workflow-id': otherWorkflow });
  assert.equal(result.sharedPolicyRoleOnly, true);
  assert.match(result.activationBlockers[0], /not attested/);
  const directory = path.dirname(f.options['host-config']);
  const prompt = await readFile(path.join(directory, 'workflow-prompt.txt'), 'utf8');
  assert.match(prompt, /owner-configured deployment assertions/);
  assert.ok(prompt.includes(`WORKFLOW=${otherWorkflow}`));
  assert.ok(!prompt.includes(workflow));
  assert.equal(JSON.parse(await readFile(f.options['host-config'], 'utf8')).verified, false);
});

test('missing tools/auth/wrong account/access/Xcode/runtime fail without staging or leaking raw diagnostics', async (t) => {
  const f = await fixture(t);
  for (const tool of ['git', 'gh', 'copilot', 'python3', 'xcode-select', 'xcodebuild', 'xcrun', 'bash']) {
    f.state.fail = (name) => name === tool;
    await assert.rejects(() => f.run({ apply: true }), (error) =>
      error.message.includes('Unmet prerequisites') && !error.message.includes('TOKEN_SHOULD_NOT_LEAK'), tool);
  }
  f.state.fail = (tool, args) => tool === 'gh' && args[0] === 'auth';
  await assert.rejects(() => f.run(), /Authenticate gh/);
  f.state.fail = () => false;
  f.state.login = 'wrong-user';
  await assert.rejects(() => f.run(), /Active gh account/);
  f.state.login = 'fixture-user';
  f.state.write = false;
  await assert.rejects(() => f.run(), /write access/);
  f.state.write = true;
  f.state.repository = 'attacker/fork';
  await assert.rejects(() => f.run(), /write access/);
  assert.deepEqual(await readdir(f.root), [path.basename(f.repo)]);
  await assert.rejects(() => f.run({}, { platform: 'linux' }), /macOS/);
  await assert.rejects(() => f.run({}, { nodeVersion: '18.0.0' }), /Node >=20/);
});

test('command wrapper suppresses raw errors and disables resolver GitHub env writes', async (t) => {
  const root = await mkdtemp(path.join(await realpath(os.tmpdir()), 'ralph command '));
  t.after(() => rm(root, { recursive: true, force: true }));
  const output = path.join(root, 'must-not-exist');
  const value = await runCommand(process.execPath, ['-e', 'console.log(process.env.GITHUB_ENV ?? "absent")'], { env: { GITHUB_ENV: output } });
  assert.equal(value, 'absent');
  await assert.rejects(() => runCommand(process.execPath, ['-e', 'console.error("private-token");process.exit(1)']),
    (error) => !error.message.includes('private-token') && error.message.includes('Raw output suppressed'));
  await assert.rejects(() => runCommand(path.join(root, 'missing'), []), /not installed/);
  const literal = 'value with spaces; $(printf injected) & --option';
  assert.equal(await runCommand(process.execPath, ['-e', 'process.stdout.write(process.argv[1])', literal]), literal);
});

test('programmatic setup rejects unsafe subprocess identities even without CLI parsing', async (t) => {
  const f = await fixture(t);
  for (const approved of ['--help', 'HEAD; whoami', 'a'.repeat(40) + '\n']) {
    await assert.rejects(validatePolicy({ ...f.options, 'approved-policy': approved }, f.command, f.approved), /subprocess inputs/);
  }
  assert.equal(f.calls.length, 0);
  const options = await nativeOptions(f);
  for (const control of ['--help', 'fixture/repo;whoami', 'fixture/repo\n', 'fixture/repo/extra']) {
    await assert.rejects(f.run({ ...options, 'control-repo': control }), /repository API input/);
  }
  assert.equal(f.calls.some((call) => call.tool === 'gh' && call.args.at(-1)?.startsWith('repos/fixture/')), false);
});
test('wrong origins, modified/staged/ignored/untracked controlled files and unrelated approved commit fail closed', async (t) => {
  const f = await fixture(t);
  for (const origin of ['https://github.com/attacker/PrintFarmer.git', 'https://github.com/OlyForge3D/PrintFarmer.git.evil',
    'https://token@github.com/OlyForge3D/PrintFarmer.git', 'file:///tmp/repo', 'https://evil.invalid/OlyForge3D/PrintFarmer']) {
    f.state.origin = origin;
    await assert.rejects(() => f.run(), /Wrong origin/);
  }
  f.state.origin = undefined;
  const tracked = path.join(f.repo, '.github/copilot-instructions.md');
  await writeFile(tracked, 'untrusted change\n');
  await assert.rejects(() => f.run(), /Policy validation blocked/);
  await f.git(['add', '.github/copilot-instructions.md']);
  await assert.rejects(() => f.run(), /Policy validation blocked/);
  await writeFile(tracked, 'approved\n');
  await f.git(['add', '.github/copilot-instructions.md']);
  await f.git(['update-index', '--assume-unchanged', '.github/copilot-instructions.md']);
  await assert.rejects(() => f.run(), /assume-unchanged/);
  await f.git(['update-index', '--no-assume-unchanged', '.github/copilot-instructions.md']);
  await f.git(['update-index', '--skip-worktree', '.github/copilot-instructions.md']);
  await assert.rejects(() => f.run(), /skip-worktree/);
  await f.git(['update-index', '--no-skip-worktree', '.github/copilot-instructions.md']);
  const untracked = path.join(f.repo, 'scripts/ci/ralph-untracked.mjs');
  await writeFile(untracked, 'untrusted\n');
  await assert.rejects(() => f.run(), /Untracked/);
  await writeFile(path.join(f.repo, '.git/info/exclude'), 'scripts/ci/ralph-untracked.mjs\n');
  await assert.rejects(() => f.run(), /Untracked/);
  await rm(untracked);
  await writeFile(path.join(f.repo, 'application.txt'), 'unrelated dirty application change\n');
  await f.run();
  const unrelated = await f.git(['commit-tree', `${f.approved}^{tree}`, '-m', 'Unrelated root']);
  await assert.rejects(() => f.run({ 'approved-policy': unrelated }), /Policy validation blocked/);
});

test('remote ancestry, changed policy, renamed policy, truncated trees and invalid SHA fail closed', async (t) => {
  const f = await fixture(t);
  for (const comparison of [
    { status: 'behind', merge_base_commit: { sha: f.approved }, files: [] },
    { status: 'diverged', merge_base_commit: { sha: 'b'.repeat(40) }, files: [] },
    { status: 'ahead', merge_base_commit: { sha: 'b'.repeat(40) }, files: [] },
    { status: 'ahead', merge_base_commit: { sha: f.approved }, files: [{ filename: '.copilot/skills/ralph-loop/hosts.json' }] },
    { status: 'ahead', merge_base_commit: { sha: f.approved }, files: [{ filename: 'moved.txt', previous_filename: 'scripts/ci/ralph-automation.mjs' }] },
  ]) {
    f.state.comparison = comparison;
    await assert.rejects(() => f.run(), /Policy validation blocked/);
  }
  f.state.comparison = { status: 'identical', merge_base_commit: { sha: f.approved }, files: [] };
  f.state.truncatedTree = true;
  await assert.rejects(() => f.run(), /tree coverage is incomplete/);
  f.state.truncatedTree = false;
  f.state.development = 'development';
  await assert.rejects(() => f.run(), /exact development SHA/);
});

test('differing old/new policy commits are not two kinds of pin; one stale pin blocks', async (t) => {
  const f = await fixture(t);
  const updatedPolicy = structuredClone(policy);
  updatedPolicy.policyVersion = 'new-approved-policy-fixture';
  await writeFile(path.join(f.repo, '.copilot/skills/ralph-loop/hosts.json'), JSON.stringify(updatedPolicy));
  await f.git(['add', '.']);
  await f.git(['commit', '-qm', 'Reviewed policy revision']);
  const newPin = await f.git(['rev-parse', 'HEAD']);
  await assert.rejects(() => f.run(), /Policy validation blocked/);
  f.state.development = newPin;
  f.state.comparison = { status: 'identical', merge_base_commit: { sha: newPin }, files: [] };
  const result = await f.run({ apply: true, 'approved-policy': newPin, 'workflow-id': otherWorkflow });
  assert.equal(result.sharedPolicyRoleOnly, true);
  const prompt = await readFile(path.join(path.dirname(f.options['host-config']), 'workflow-prompt.txt'), 'utf8');
  assert.ok(prompt.includes(`POLICY_COMMIT=${newPin}`));
  assert.ok(prompt.includes(`--approved-policy ${newPin}`));
  assert.ok(!prompt.includes(f.approved));
});

test('legacy deployment pins, invalid role policies and changed bootstrap templates cannot be staged', async (t) => {
  const f = await fixture(t);
  const commit = async () => {
    await f.git(['add', '.']);
    await f.git(['commit', '-qm', 'Policy variant fixture']);
    const approved = await f.git(['rev-parse', 'HEAD']);
    f.state.development = approved;
    f.state.comparison = { status: 'identical', merge_base_commit: { sha: approved }, files: [] };
    return approved;
  };
  for (const override of [{ workflowId: workflow }, { configured: false }, { scope: 'general' }, { maxLocalXcodeJobs: 2 }]) {
    const modified = structuredClone(policy);
    Object.assign(modified.hosts['macos-mobile'], override);
    await writeFile(path.join(f.repo, '.copilot/skills/ralph-loop/hosts.json'), JSON.stringify(modified));
    const approved = await commit();
    await assert.rejects(() => f.run({ apply: true, 'approved-policy': approved }), /Policy validation blocked/);
  }
  for (const override of [{ workflowId: workflow }, { configured: false }, { maxMobileSessions: 1 }, { maxGeneralSessions: 6 }, { newRemoteMobileDispatch: true }]) {
    const modified = structuredClone(policy);
    Object.assign(modified.hosts['windows-general'], override);
    await writeFile(path.join(f.repo, '.copilot/skills/ralph-loop/hosts.json'), JSON.stringify(modified));
    const approved = await commit();
    await assert.rejects(() => f.run({ apply: true, 'approved-policy': approved }), /Policy validation blocked/);
  }
  await writeFile(path.join(f.repo, '.copilot/skills/ralph-loop/hosts.json'), JSON.stringify(policy));
  await writeFile(path.join(f.repo, '.copilot/skills/ralph-loop/bootstrap.md'), 'Unsupported bootstrap\n');
  const approved = await commit();
  await assert.rejects(() => f.run({ apply: true, 'approved-policy': approved }), /template is unsupported/);
  await assert.rejects(() => lstat(f.options['host-config']), { code: 'ENOENT' });
});

test('symlink ancestors/files, overlapping paths, files in checkouts, populated caches and overwrite are refused', async (t) => {
  const f = await fixture(t);
  await mkdir(path.join(f.root, 'outside'));
  await symlink(path.join(f.root, 'outside'), path.join(f.root, 'alias'));
  await assert.rejects(() => f.run({ 'host-config': path.join(f.root, 'alias/host.json') }), /Symlink/);
  await assert.rejects(() => f.run({ 'cache-dir': path.join(f.repo, 'cache') }), /disjoint/);
  await mkdir(path.join(f.root, 'other-checkout', '.git'), { recursive: true });
  await assert.rejects(() => f.run({ 'host-config': path.join(f.root, 'other-checkout/private/host.json') }), /outside all checkouts/);
  await mkdir(f.options['cache-dir']);
  await writeFile(path.join(f.options['cache-dir'], 'historical.json'), '{}');
  await assert.rejects(() => f.run(), /new or empty/);
  await rm(path.join(f.options['cache-dir'], 'historical.json'));
  await f.run({ apply: true });
  const target = f.options['host-config'];
  const original = await readFile(target, 'utf8');
  await writeFile(target, original.replace('"verified": false', '"verified": true'));
  await assert.rejects(() => f.run({ apply: true }), /overwrite differing/);
  assert.equal(JSON.parse(await readFile(target, 'utf8')).verified, true);
  await writeFile(target, original);
  await chmod(target, 0o644);
  await assert.rejects(() => f.run(), /mode 0600/);
  await rm(target);
  const linked = path.join(f.root, 'linked-config');
  await writeFile(linked, original, { mode: 0o600 });
  await link(linked, target);
  await assert.rejects(() => f.run(), /Unsafe file/);
  await rm(target);
  await symlink(path.join(f.root, 'outside/file'), target);
  await assert.rejects(() => f.run(), /Symlink/);
});

test('unsafe output permissions, output name collision and partial package conflicts never overwrite or attest', async (t) => {
  const f = await fixture(t);
  const directory = path.dirname(f.options['host-config']);
  await mkdir(directory, { mode: 0o700 });
  await chmod(directory, 0o777);
  await assert.rejects(() => f.run({ apply: true }), /group\/world-writable/);
  await chmod(directory, 0o700);
  await assert.rejects(() => f.run({ 'host-config': path.join(directory, 'workflow-settings.json') }), /filename conflicts/);
  await writeFile(path.join(directory, 'workflow-settings.json'), 'preserve existing\n', { mode: 0o600 });
  await assert.rejects(() => f.run({ apply: true }), /overwrite differing/);
  await assert.rejects(() => lstat(f.options['host-config']), { code: 'ENOENT' });
  assert.equal(await readFile(path.join(directory, 'workflow-settings.json'), 'utf8'), 'preserve existing\n');
});

test('default discovery displays exact scope then persists one coherent explicitly confirmed pin', async (t) => {
  const f = await fixture(t);
  let confirmations = 0;
  const result = await f.run({ apply: true, 'approved-policy': undefined }, {
    confirm: async (review) => {
      confirmations++;
      assert.equal(review.exactCommit, f.approved);
      assert.equal(review.observedDevelopment, f.approved);
      assert.ok(review.controlledPaths.includes('.copilot/skills/ralph-loop'));
      assert.ok(review.controlledFiles.some((file) => file.path === 'scripts/ci/ralph-automation.mjs' && /^[0-9a-f]{40}$/.test(file.sha)));
      assert.equal(review.mobileRole.maxLocalXcodeJobs, 1);
      assert.match(review.changeSummary[0], /Initial approval/);
      assert.match(review.authorizes, /NOT native identity/);
      await assert.rejects(() => lstat(f.options['host-config']), { code: 'ENOENT' });
      assert.equal(f.calls.filter(({ tool }) => tool === 'bash').length, 0);
      return true;
    },
  });
  assert.equal(confirmations, 1);
  assert.equal(result.policyApproval, 'explicitly-approved');
  const directory = path.dirname(f.options['host-config']);
  const receipt = JSON.parse(await readFile(path.join(directory, 'policy-approval.json'), 'utf8'));
  assert.equal(receipt.approvedCommit, f.approved);
  assert.equal(receipt.repository, 'OlyForge3D/PrintFarmer');
  assert.match(receipt.controlledContentSha256, /^[0-9a-f]{64}$/);
  const prompt = await readFile(path.join(directory, 'workflow-prompt.txt'), 'utf8');
  assert.ok(prompt.includes(`POLICY_COMMIT=${receipt.approvedCommit}`));
  assert.ok(prompt.includes(`--approved-policy ${receipt.approvedCommit}`));
});

test('first approval refusal, headless execution and dry-run never persist approval or execute candidate code', async (t) => {
  const f = await fixture(t);
  await assert.rejects(() => f.run({ apply: true, 'approved-policy': undefined }, { confirm: async () => false }), /approval refused/);
  await assert.rejects(() => f.run({ apply: true }, { confirm: confirmPolicy }), /interactive terminal/);
  const result = await f.run({ 'approved-policy': undefined }, { confirm: async () => assert.fail('dry-run must not prompt') });
  assert.equal(result.status, 'approval-required');
  assert.equal(result.policyReview.exactCommit, f.approved);
  assert.deepEqual(await readdir(f.root), [path.basename(f.repo)]);
  assert.equal(f.calls.some(({ tool }) => tool === 'bash'), false);
});

test('saved approval is reused without confirmation across unrelated development commits', async (t) => {
  const f = await fixture(t);
  const first = await f.run({ apply: true, 'approved-policy': undefined });
  const before = await Promise.all(first.files.map(async (file) => [await readFile(file, 'utf8'), (await lstat(file)).mtimeMs]));
  await writeFile(path.join(f.repo, 'unrelated.txt'), 'unrelated app change\n');
  await f.git(['add', 'unrelated.txt']);
  await f.git(['commit', '-qm', 'Application only']);
  f.state.development = await f.git(['rev-parse', 'HEAD']);
  f.state.comparison = {
    status: 'ahead', merge_base_commit: { sha: f.approved }, files: Array.from({ length: 300 }, (_, i) => ({ filename: `src/${i}.cs` })),
  };
  const result = await f.run({ apply: true, 'approved-policy': undefined }, {
    confirm: async () => assert.fail('unchanged content must reuse the saved approval'),
  });
  assert.equal(result.policyApproval, 'reused');
  assert.equal(result.approvedOuterAndPreflightCommit, f.approved);
  assert.notEqual(result.development, f.approved);
  const after = await Promise.all(first.files.map(async (file) => [await readFile(file, 'utf8'), (await lstat(file)).mtimeMs]));
  assert.deepEqual(after, before);
  assert.equal((await f.run({ 'approved-policy': undefined })).status, 'validated-not-attested');
});

test('controlled changes block reuse; explicit informed renewal writes a new package and preserves baseline', async (t) => {
  const f = await fixture(t);
  const first = await f.run({ apply: true, 'approved-policy': undefined });
  const previousPath = path.join(path.dirname(f.options['host-config']), 'policy-approval.json');
  const before = await Promise.all(first.files.map((file) => readFile(file, 'utf8')));
  await writeFile(path.join(f.repo, '.github/copilot-instructions.md'), 'new approved safety rule\n');
  await f.git(['add', '.github/copilot-instructions.md']);
  await f.git(['commit', '-qm', 'Policy safety change']);
  const candidate = await f.git(['rev-parse', 'HEAD']);
  f.state.development = candidate;
  f.state.comparison = { status: 'ahead', merge_base_commit: { sha: f.approved }, files: [{ filename: '.github/copilot-instructions.md' }] };
  await assert.rejects(() => f.run({ apply: true, 'approved-policy': undefined }, {
    confirm: async () => assert.fail('changed policy must not silently renew'),
  }), /Saved approval cannot be reused/);
  await assert.rejects(() => f.run({ apply: true, 'approved-policy': candidate }), /Explicit pin differs/);
  await assert.rejects(() => f.run({ apply: true, 'renew-policy': true, 'previous-approval': previousPath }), /NEW --host-config directory/);
  f.state.comparison = { status: 'identical', merge_base_commit: { sha: candidate }, files: [] };
  const options = {
    apply: true, 'approved-policy': undefined, 'renew-policy': true, 'previous-approval': previousPath,
    'host-config': path.join(f.root, 'renewal package', 'host.json'),
  };
  await assert.rejects(() => f.run(options, { confirm: async () => false }), /approval refused/);
  const result = await f.run(options, {
    confirm: async (review) => {
      assert.equal(review.previousApproval, f.approved);
      assert.equal(review.exactCommit, candidate);
      assert.ok(review.changeSummary.some((line) => line.includes('copilot-instructions.md')));
      assert.ok(review.reviewCommand.includes(f.approved));
      assert.ok(review.reviewCommand.includes(candidate));
      return true;
    },
  });
  assert.equal(result.approvedOuterAndPreflightCommit, candidate);
  assert.deepEqual(await Promise.all(first.files.map((file) => readFile(file, 'utf8'))), before);
  assert.equal(JSON.parse(await readFile(options['host-config'], 'utf8')).verified, false);
});

test('candidate movement, local edits and concurrent receipt creation invalidate an in-flight approval before writes', async (t) => {
  for (const change of ['remote', 'local', 'receipt']) {
    await t.test(change, async (t) => {
      const f = await fixture(t);
      await assert.rejects(() => f.run({ apply: true, 'approved-policy': undefined }, {
        confirm: async () => {
          if (change === 'remote') f.state.development = 'b'.repeat(40);
          if (change === 'local') await writeFile(path.join(f.repo, '.github/copilot-instructions.md'), 'raced edit\n');
          if (change === 'receipt') {
            const directory = path.dirname(f.options['host-config']);
            await mkdir(directory, { mode: 0o700 });
            await writeFile(path.join(directory, 'policy-approval.json'), '{}', { mode: 0o600 });
          }
          return true;
        },
      }), /changed during|Command failed|invalid identity/);
      await assert.rejects(() => lstat(f.options['host-config']), { code: 'ENOENT' });
      assert.equal(f.calls.some(({ tool }) => tool === 'bash'), false);
    });
  }
});

test('clone is opt-in, dry-run never clones, apply clones only the fixed trusted origin and retains failed clone for inspection', async (t) => {
  const f = await fixture(t);
  await assert.rejects(() => f.run({ clone: true, apply: true }), /nonexistent/);
  const repo = path.join(f.root, 'new clone');
  await assert.rejects(() => f.run({ repo }), /Repository missing/);
  const result = await f.run({ repo, clone: true });
  assert.equal(result.status, 'clone-pending');
  assert.equal(f.calls.some(({ args }) => args.includes('clone')), false);
  const command = async (tool, args, settings) => {
    if (tool === 'git' && args.includes('clone')) {
      assert.deepEqual(args.slice(-3), ['--', 'https://github.com/OlyForge3D/PrintFarmer.git', repo]);
      assert.ok(args.includes('core.hooksPath=/dev/null'));
      await mkdir(repo);
      throw new Error('fixture failed network clone');
    }
    return f.command(tool, args, settings);
  };
  await assert.rejects(() => f.run({ repo, clone: true, apply: true }, { command }), /failed network clone/);
  assert.equal((await lstat(repo)).isDirectory(), true);
  await assert.rejects(() => lstat(f.options['host-config']), { code: 'ENOENT' });
});
