import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import yaml from 'js-yaml';
import { runBaseline, serializeBaseline } from '../baseline-ui-design.mjs';
import { allowancePath, baselinePath, approveBaseline, evaluateCountCeiling,
  findingCounts, runCountRatchet, validateAllowance } from '../ui-count-ratchet.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');

function fixture(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'printfarmer-ratchet-test-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  fs.cpSync(path.join(root, 'scripts/design'), path.join(directory, 'scripts/design'), { recursive: true });
  const write = (file, source) => {
    fs.mkdirSync(path.dirname(path.join(directory, file)), { recursive: true });
    fs.writeFileSync(path.join(directory, file), source);
  };
  const git = args => {
    const result = spawnSync('git', args, { cwd: directory, encoding: 'utf8', windowsHide: true });
    assert.equal(result.status, 0, result.stderr);
    return result.stdout.trim();
  };
  git(['init', '--quiet']);
  git(['config', 'user.name', 'Ratchet Fixture']);
  git(['config', 'user.email', 'ratchet@example.invalid']);
  git(['config', 'core.autocrlf', 'false']);
  write('.impeccable/config.json', '{}');
  write('DESIGN.md', '---\nname: Fixture\ncolors:\n  accent: "#ffffff"\n---\n');
  write('src/Web/ReactApp/package.json', '{}');
  write('src/Web/ReactApp/src/App.tsx', '<div className="border-l-4 rounded-lg">A</div>');
  write('mobile/PrintFarmer/Views/Button.swift', 'Button("Save") {}.frame(width: 32, height: 32)');
  write('site/index.html', '<div class="border-l-4 rounded-lg">Site</div>');
  write('site/styles.css', 'div { color: #123456; }');
  const commit = () => {
    git(['add', '--all']);
    git(['commit', '--quiet', '-m', 'Trusted fixture']);
    return git(['rev-parse', 'HEAD']);
  };
  const source = commit();
  const report = runBaseline({ root: directory, commit: source });
  write(baselinePath, serializeBaseline(report));
  const policy = approveBaseline({ root: directory, ceiling: findingCounts(report).total });
  const base = commit();
  const target = edits => {
    for (const [file, content] of Object.entries(edits)) write(file, content);
    git(['add', '--all']);
    const tree = git(['write-tree']);
    const head = git(['commit-tree', tree, '-p', base, '-m', 'Untrusted target']);
    git(['read-tree', '--reset', '-u', base]);
    return head;
  };
  return { directory, source, base, report, policy, target, write, git };
}

function account(report) {
  for (const [disposition, name] of [['blocker', 'blockers'], ['advisory', 'advisories'], ['suppressed', 'suppressed']]) {
    report.summary[name] = report.findings.filter(finding => finding.disposition === disposition).length;
    for (const platform of ['web', 'ios', 'site']) {
      report.summary.platforms[platform][name] = report.findings.filter(finding =>
        finding.platform === platform && finding.disposition === disposition).length;
    }
  }
  return report;
}

test('equal/decreased/increased TOTAL counts and severity redistribution follow the approved policy', t => {
  const f = fixture(t);
  const bytes = Buffer.from(serializeBaseline(f.report));
  const equal = evaluateCountCeiling(f.policy, bytes, f.report, f.report);
  assert.equal(equal.status, 'pass');
  assert.equal(equal.observed.total, equal.observed.blockers + equal.observed.advisories);
  const decreased = structuredClone(f.report);
  decreased.findings.pop();
  assert.equal(evaluateCountCeiling(f.policy, bytes, f.report, account(decreased)).status, 'pass');
  const increased = structuredClone(f.report);
  increased.findings.push({ ...increased.findings[0], line: 99 });
  assert.equal(evaluateCountCeiling(f.policy, bytes, f.report, account(increased)).status, 'fail');
  const swapped = structuredClone(f.report);
  swapped.findings[0] = { ...swapped.findings[0], antipattern: 'bounce-easing', disposition: 'advisory', line: 50 };
  const swap = evaluateCountCeiling(f.policy, bytes, f.report, account(swapped));
  assert.equal(swap.status, 'pass');
  assert.equal(swap.observed.total, equal.observed.total);
  assert.notEqual(swap.observed.advisories, equal.observed.advisories);
  assert.match(swap.limitation, /Same-total swaps/);
});

test('trusted execution scans immutable targets, ignores PR tooling/allowance/policy tampering and preserves total swaps', t => {
  const f = fixture(t);
  assert.equal(runCountRatchet({ root: f.directory, trustedBase: f.base, head: f.base }).status, 'pass');
  const head = f.target({
    'scripts/design/ui-count-ratchet.mjs': 'throw new Error("PR tooling must never execute");',
    'scripts/design/baseline-ui-design.mjs': 'throw new Error("PR scanner must never execute");',
    [allowancePath]: '{"ceiling":99999}',
    [baselinePath]: '{"findings":[]}',
    '.impeccable/config.json': '{"detector":{"designSystem":{"enabled":false}}}',
    'DESIGN.md': 'Not valid frontmatter; untrusted policy must not be consumed.',
    'src/Web/ReactApp/src/App.tsx': '<div className="animate-bounce">Swap</div>',
  });
  const result = runCountRatchet({ root: f.directory, trustedBase: f.base, head });
  assert.equal(result.status, 'pass');
  assert.equal(result.observed.total, f.policy.ceiling);
  assert.equal(result.inspectedCommit, head);
  assert.equal(result.policyCommit, f.base);
  assert.equal(result.trustedBase, f.base);
  assert.ok(result.report.findings.some(finding => finding.antipattern === 'bounce-easing'));
  assert.ok(!result.report.findings.some(finding => finding.file === 'src/Web/ReactApp/src/App.tsx' &&
    finding.antipattern === 'side-tab'));
});

test('baseline provenance must match the scanner committed in the trusted base', t => {
  const f = fixture(t);
  f.write('scripts/design/check-ui-design.mjs', 'export const scannerVersion = "changed";\n');
  f.git(['add', 'scripts/design/check-ui-design.mjs']);
  f.git(['commit', '--quiet', '-m', 'Change trusted scanner']);
  const trustedBase = f.git(['rev-parse', 'HEAD']);

  assert.throws(
    () => runCountRatchet({ root: f.directory, trustedBase, head: trustedBase }),
    /Baseline tooling provenance mismatch: scripts\/design\/check-ui-design\.mjs/,
  );
});

test('committed repository baseline and allowance match their pinned tooling', () => {
  const git = args => {
    const result = spawnSync('git', args, {
      cwd: root,
      encoding: 'utf8',
      windowsHide: true,
      maxBuffer: 10 * 1024 * 1024,
    });
    assert.equal(result.status, 0, result.error?.message ?? result.stderr);
    return result.stdout;
  };
  const head = git(['rev-parse', 'HEAD']).trim();
  const baselineBytes = Buffer.from(git(['show', `${head}:${baselinePath}`]));
  const policy = JSON.parse(git(['show', `${head}:${allowancePath}`]));
  const baseline = JSON.parse(baselineBytes);

  assert.equal(policy.baselineSha256, hash(baselineBytes));
  for (const input of baseline.provenance.tooling.files) {
    const committedTool = Buffer.from(git(['show', `${head}:${input.file}`]));
    assert.equal(hash(committedTool), input.sha256, `${input.file} must match its committed provenance pin`);
  }

  const result = runCountRatchet({ root, trustedBase: head, head });
  assert.equal(result.status, 'pass');
  assert.equal(result.baselineSha256, policy.baselineSha256);
});

test('PR-added shared and inline waivers cannot buy a lower total or hide new debt', t => {
  const f = fixture(t);
  const head = f.target({
    '.impeccable/config.json': '{"detector":{"ignoreValues":[{"rule":"side-tab","value":"*","files":["*"],"reason":"PR says trust me"}]}}',
    'src/Web/ReactApp/src/New.tsx': '// impeccable-disable -- PR says trust me\n<div className="border-l-4 rounded-lg">New</div>',
  });
  const result = runCountRatchet({ root: f.directory, trustedBase: f.base, head });
  assert.equal(result.status, 'fail');
  assert.equal(result.observed.total, f.policy.ceiling + 1);
  assert.equal(result.report.findings.find(finding => finding.file.endsWith('New.tsx')).disposition, 'blocker');
});

test('scanner input failures and unexpected loss of platform capabilities fail clearly', t => {
  const f = fixture(t);
  const badEncoding = f.target({ 'site/styles.css': Buffer.from([0xff]) });
  assert.throws(() => runCountRatchet({ root: f.directory, trustedBase: f.base, head: badEncoding }), /UTF-8/);
  f.git(['rm', '--quiet', 'mobile/PrintFarmer/Views/Button.swift']);
  const tree = f.git(['write-tree']);
  const missing = f.git(['commit-tree', tree, '-p', f.base, '-m', 'Missing capability']);
  f.git(['read-tree', '--reset', '-u', f.base]);
  assert.throws(() => runCountRatchet({ root: f.directory, trustedBase: f.base, head: missing }), /no supported tracked ios/);
  assert.throws(() => runCountRatchet({ root: f.directory, trustedBase: 'main', head: f.base }), /immutable/);
  assert.throws(() => runCountRatchet({ root: f.directory, trustedBase: f.source, head: f.base }), /checkout/);
  f.write('scripts/design/swiftui-targets.mjs', 'export function detectSmallSwiftUiTargets(){return [];}');
  assert.throws(() => runCountRatchet({ root: f.directory, trustedBase: f.base, head: f.base }), /Executing tooling/);
});

test('malformed baselines, incorrect severity accounting, invalid provenance and allowance increases fail closed', t => {
  const f = fixture(t);
  const bytes = Buffer.from(serializeBaseline(f.report));
  for (const policy of [
    { ...f.policy, ceiling: f.policy.ceiling + 1 },
    { ...f.policy, schemaVersion: 2 },
    { ...f.policy, baselineSha256: '0'.repeat(64) },
    { ...f.policy, snapshot: { ...f.policy.snapshot, blockers: -1 } },
  ]) assert.throws(() => validateAllowance(policy, bytes, f.report), /allowance|snapshot|digest|count/i);
  const changed = structuredClone(f.report);
  changed.summary.blockers += 1;
  assert.throws(() => findingCounts(changed), /accounting/);
  const provenance = structuredClone(f.report);
  provenance.provenance.tooling.files[0].sha256 = 'invalid';
  assert.throws(() => findingCounts(provenance), /provenance/);
  const scope = structuredClone(f.report);
  scope.coverage.roots.site = 'not-the-website/';
  assert.throws(() => evaluateCountCeiling(f.policy, bytes, f.report, scope), /Incompatible/);
  const hashChanged = structuredClone(f.report);
  hashChanged.provenance.tooling.files[0].sha256 = '0'.repeat(64);
  assert.throws(() => evaluateCountCeiling(f.policy, bytes, f.report, hashChanged), /Incompatible/);
});

test('even a rehashed trusted report with invented findings/provenance must reproduce exactly', t => {
  const f = fixture(t);
  const fake = structuredClone(f.report);
  fake.findings[0].snippet = 'Invented observation';
  const bytes = serializeBaseline(fake);
  const head = f.target({
    [baselinePath]: bytes,
    [allowancePath]: JSON.stringify({ ...f.policy, baselineSha256: hash(bytes) }),
  });
  f.git(['checkout', '--quiet', '--detach', head]);
  assert.throws(() => runCountRatchet({ root: f.directory, trustedBase: head, head }), /cannot be reproduced/);
});

test('baseline approval is deliberate, reproducible and cannot increase an existing ceiling', t => {
  const f = fixture(t);
  assert.equal(approveBaseline({ root: f.directory, ceiling: f.policy.ceiling }).ceiling, f.policy.ceiling);
  assert.throws(() => approveBaseline({ root: f.directory, ceiling: f.policy.ceiling + 1 }), /observed/);
  const head = f.target({ 'src/Web/ReactApp/src/New.tsx': '<div className="animate-bounce">New</div>' });
  const report = runBaseline({ root: f.directory, commit: head });
  f.write(baselinePath, serializeBaseline(report));
  assert.throws(() => approveBaseline({ root: f.directory, ceiling: findingCounts(report).total }), /cannot increase/);
  const lowerHead = f.target({ 'src/Web/ReactApp/src/App.tsx': '<div>Debt removed</div>' });
  const lower = runBaseline({ root: f.directory, commit: lowerHead });
  f.write(baselinePath, serializeBaseline(lower));
  const lowered = approveBaseline({ root: f.directory, ceiling: findingCounts(lower).total });
  assert.equal(lowered.ceiling, f.policy.ceiling - 1);
  assert.equal(lowered.snapshot.total, lowered.ceiling);
});

test('CLI returns a failing count result with complete evidence and refuses ambiguous or missing arguments', t => {
  const f = fixture(t);
  const head = f.target({ 'src/Web/ReactApp/src/New.tsx': '<div className="animate-bounce">New</div>' });
  const script = path.join(f.directory, 'scripts/design/ui-count-ratchet.mjs');
  const output = path.join(f.directory, 'result.json');
  const run = args => spawnSync(process.execPath, [script, ...args],
    { cwd: f.directory, encoding: 'utf8', windowsHide: true });
  const result = run(['--trusted-base', f.base, '--head', head, '--output', output]);
  assert.equal(result.status, 1, result.stderr);
  const report = JSON.parse(fs.readFileSync(output, 'utf8'));
  assert.equal(report.status, 'fail');
  assert.equal(report.observed.total, f.policy.ceiling + 1);
  assert.equal(report.report.inspectedCommit, head);
  for (const args of [[], ['--approve-baseline'], ['--trusted-base', f.base],
    ['--head', head, '--head', head], ['--approve-baseline', '--ceiling', '1', '--head', head]]) {
    assert.equal(run(args).status, 1);
  }
});

test('approval refuses a concurrent writer without changing its allowance or removing its lock', t => {
  const f = fixture(t);
  const destination = path.join(f.directory, allowancePath);
  const original = fs.readFileSync(destination);
  const lock = `${destination}.lock`;
  fs.writeFileSync(lock, 'Other approval owns this lock', { flag: 'wx' });
  assert.throws(() => approveBaseline({ root: f.directory, ceiling: f.policy.ceiling }), { code: 'EEXIST' });
  assert.deepEqual(fs.readFileSync(destination), original);
  assert.equal(fs.readFileSync(lock, 'utf8'), 'Other approval owns this lock');
});

test('approval preserves malformed allowances, propagates read errors and releases only its own lock', t => {
  const f = fixture(t);
  const destination = path.join(f.directory, allowancePath);
  f.write(allowancePath, '{"schemaVersion":2}');
  assert.throws(() => approveBaseline({ root: f.directory, ceiling: f.policy.ceiling }), /malformed/);
  assert.equal(fs.readFileSync(destination, 'utf8'), '{"schemaVersion":2}');
  assert.deepEqual(fs.readdirSync(path.dirname(destination)), ['config.json', 'ui-count-ceiling.json']);
  fs.unlinkSync(destination);
  fs.mkdirSync(destination);
  assert.throws(() => approveBaseline({ root: f.directory, ceiling: f.policy.ceiling }), { code: 'EISDIR' });
  assert.deepEqual(fs.readdirSync(path.dirname(destination)), ['config.json', 'ui-count-ceiling.json']);
});

test('trusted CI workflow uses no PR checkout/install, binds statuses to head and leaves changed-file checks intact', () => {
  const workflow = fs.readFileSync(path.join(root, '.github/workflows/ui-count-ratchet.yml'), 'utf8');
  const parsed = yaml.load(workflow);
  assert.ok(parsed.on.pull_request_target);
  assert.deepEqual(parsed.permissions, { contents: 'read' });
  const job = parsed.jobs['count-ratchet'];
  assert.deepEqual(job.permissions, { contents: 'read', statuses: 'write' });
  assert.equal(job.steps[0].with.ref, '${{ github.event.repository.default_branch }}');
  assert.equal(job.steps[0].with['persist-credentials'], false);
  assert.equal(job['timeout-minutes'], 10);
  assert.match(workflow, /pull_request_target:/);
  assert.match(workflow, /ref: \$\{\{ github\.event\.repository\.default_branch \}\}/);
  assert.match(workflow, /persist-credentials: false/);
  assert.match(workflow, /refs\/pull\/\$\{number\}\/head/);
  assert.match(workflow, /FETCH_HEAD/);
  assert.match(workflow, /--trusted-base "\$TRUSTED_BASE_SHA" --head "\$TARGET_HEAD_SHA"/);
  assert.match(workflow, /sha: head, state, context: 'ui\/full-source-count'/);
  assert.match(workflow, /if: always\(\)/);
  assert.doesNotMatch(workflow, /npm (ci|install)|secrets\.|checkout.*head|pull_request\.head\.repo/);
  for (const action of workflow.matchAll(/^\s+(?:-\s+)?uses: ([^\s#]+)/gm)) {
    assert.match(action[1], /@[a-f0-9]{40}$/);
  }
  assert.ok(fs.readFileSync(path.join(root, '.github/workflows/ci.yml'), 'utf8')
    .includes('scripts/design/check-ui-design.mjs'));
  assert.ok(fs.readFileSync(path.join(root, '.gitignore'), 'utf8').split(/\r?\n/)
    .includes('!/.impeccable/ui-count-ceiling.json'));
});

test('trusted workflow emits failure on malformed evidence and never reuses a green status after scanning fails', async () => {
  const workflow = yaml.load(fs.readFileSync(path.join(root, '.github/workflows/ui-count-ratchet.yml'), 'utf8'));
  const steps = workflow.jobs['count-ratchet'].steps;
  const script = steps.find(step => step.name === 'Record exact-head count result').with.script;
  const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
  const run = new AsyncFunction('require', 'process', 'core', 'github', 'context', script);
  const head = 'a'.repeat(40);
  const base = 'b'.repeat(40);
  const execute = async (contents, outcome = 'success') => {
    const statuses = [];
    const errors = [];
    const summary = { addHeading() { return this; }, addRaw() { return this; }, async write() {} };
    await run(name => name === 'node:fs'
      ? { existsSync: () => contents !== undefined, readFileSync: () => contents }
      : path,
    { env: { TARGET_HEAD_SHA: head, TRUSTED_BASE_SHA: base, RUNNER_TEMP: 'temporary', SCAN_OUTCOME: outcome } },
    { summary, setFailed: message => errors.push(message) },
    { rest: { repos: { createCommitStatus: async status => statuses.push(status) } } },
    { repo: { owner: 'Fixture', repo: 'Repo' }, serverUrl: 'https://github.com', runId: 1 });
    assert.equal(statuses.length, 1);
    assert.equal(statuses[0].sha, head);
    return { status: statuses[0], errors };
  };
  const passing = JSON.stringify({ inspectedCommit: head, trustedBase: base, status: 'pass',
    observed: { total: 88, blockers: 54, advisories: 34 }, ceiling: 88 });
  assert.equal((await execute(passing)).status.state, 'success');
  assert.equal((await execute(passing, 'failure')).status.state, 'failure');
  assert.equal((await execute(undefined, 'failure')).status.state, 'failure');
  const malformed = await execute('{');
  assert.equal(malformed.status.state, 'failure');
  assert.equal(malformed.errors.length, 1);
  assert.equal((await execute(passing.replace(head, base))).status.state, 'failure');
});
