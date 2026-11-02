import { createHash, randomUUID } from 'node:crypto';
import { execFileSync, spawn } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

const repo = resolve(import.meta.dirname, '..', '..', '..', '..', '..');
const inputs = [
  'src/Web/ReactApp/playwright.slicer-settings.config.ts',
  'src/Web/ReactApp/e2e/slicer-settings/settings-to-gcode.spec.ts',
  'src/Web/ReactApp/e2e/slicer-settings/gcode-evidence.ts',
  'src/Web/ReactApp/e2e/slicer-settings/run-context.ts',
  'src/Web/ReactApp/e2e/slicer-settings/isolation-preflight.cs',
  'src/Web/ReactApp/e2e/slicer-settings/post-process-proof.mjs',
  'src/Web/ReactApp/src/features/slicer/hooks/useSlicerViewMode.ts',
  'src/Web/ReactApp/src/features/slicer/generated/orcaSettingsMetadata.json',
];

export interface SourceIdentity {
  schema: 1;
  sourceSha: string;
  treeSha: string;
  harnessSha256: string;
  registrySha256: string;
}

export interface RunContext {
  root: string;
  runId: string;
  launcherPid: number;
  source: SourceIdentity;
}

function hash(value: string): string {
  return createHash('sha256').update(value.replaceAll('\r\n', '\n')).digest('hex');
}

export function sourceIdentity(repository = repo, selectedInputs = inputs): SourceIdentity {
  const git = (...args: string[]) => execFileSync('git', args, { cwd: repository, encoding: 'utf8' }).trim();
  const sourceSha = git('rev-parse', 'HEAD');
  if (!/^[a-f\d]{40}$/.test(sourceSha)) throw new Error('Cannot establish a full source revision');
  if (git('-c', 'core.fsmonitor=false', 'status', '--porcelain', '--untracked-files=no') !== '') {
    throw new Error('Commit or reconcile tracked source changes before live evidence generation');
  }
  const hashes = selectedInputs.map(path => {
    const content = readFileSync(resolve(repository, path), 'utf8');
    if (hash(content) !== hash(execFileSync('git', ['show', `HEAD:${path}`], { cwd: repository, encoding: 'utf8' }))) {
      throw new Error(`${path}: uncommitted input cannot produce reviewed-head evidence`);
    }
    return [path, hash(content)];
  });
  return {
    schema: 1,
    sourceSha,
    treeSha: git('rev-parse', 'HEAD^{tree}'),
    harnessSha256: hash(JSON.stringify(hashes)),
    registrySha256: hashes.at(-1)![1],
  };
}

// A separate process observes edits even while the launcher is blocked in a
// synchronous .NET build. Restoring a file cannot erase the latched failure.
const monitorProgram = String.raw`
const fs = require('node:fs');
const path = require('node:path');
const [repository, root, launcher, runId] = process.argv.slice(1);
const normalize = name => process.platform === 'win32' ? name.toLowerCase() : name;
const tracked = new Set();
for (const file of JSON.parse(fs.readFileSync(path.join(root, 'source-guard-inputs.json'), 'utf8'))) {
  const parts = file.split('/');
  while (parts.length) { tracked.add(normalize(parts.join('/'))); parts.pop(); }
}
const failure = path.join(root, 'source-guard-failed.json');
function fail(reason) {
  if (!fs.existsSync(failure)) fs.writeFileSync(failure, JSON.stringify({ runId, reason }), { flag: 'wx' });
}
const watcher = fs.watch(repository, { recursive: true }, (event, name) => {
  if (!name) return fail('Source watcher returned an event without a path');
  const relative = normalize(name.replaceAll('\\', '/'));
  if (tracked.has(relative)) {
    fail('Tracked source changed during this run: ' + relative);
  }
});
watcher.on('error', error => fail('Source watcher failed: ' + error.message));
process.on('uncaughtException', error => { fail(error.message); process.exit(1); });
fs.writeFileSync(path.join(root, 'source-guard-ready.json'), JSON.stringify({ runId, pid: process.pid }), { flag: 'wx' });
setInterval(() => {
  try { process.kill(Number(launcher), 0); } catch { watcher.close(); process.exit(0); }
}, 500);
`;

export function startSourceGuard(context: RunContext, repository = repo): () => void {
  const tracked = execFileSync('git', ['ls-files', '-z'], { cwd: repository, encoding: 'utf8' }).split('\0').filter(Boolean);
  writeFileSync(join(context.root, 'source-guard-inputs.json'), JSON.stringify(tracked), { flag: 'wx' });
  const child = spawn(process.execPath, ['-e', monitorProgram, repository, context.root, String(process.pid), context.runId], {
    stdio: 'ignore', windowsHide: true,
  });
  const stop = () => { child.kill(); };
  process.once('exit', stop);
  child.unref();
  const deadline = Date.now() + 10_000;
  while (!existsSync(join(context.root, 'source-guard-ready.json'))) {
    if (Date.now() > deadline) {
      stop();
      throw new Error('Source guard failed to start; refusing live evidence');
    }
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 20);
  }
  return () => { process.removeListener('exit', stop); stop(); };
}

export function assertSourceGuard(context: RunContext): void {
  const failure = join(context.root, 'source-guard-failed.json');
  if (existsSync(failure)) throw new Error(`Source provenance invalidated: ${readFileSync(failure, 'utf8')}`);
  const ready: { runId: string; pid: number } = JSON.parse(readFileSync(join(context.root, 'source-guard-ready.json'), 'utf8'));
  if (ready.runId !== context.runId || !Number.isInteger(ready.pid)) throw new Error('Invalid source guard handoff');
  try {
    process.kill(ready.pid, 0);
  } catch (error) {
    throw new Error('Source guard is no longer alive; refusing live evidence', { cause: error });
  }
}

export function assertRunSource(context: RunContext): void {
  assertSourceGuard(context);
  assertSourceIdentity(context.source, sourceIdentity());
}

export function assertSourceIdentity(expected: SourceIdentity, actual: SourceIdentity): void {
  if (JSON.stringify(expected) !== JSON.stringify(actual)) throw new Error('Source provenance changed or does not match this run');
}

export function initializeRun(
  parent: string,
  source: SourceIdentity,
  environment: NodeJS.ProcessEnv = process.env,
  pid = process.pid,
  parentPid = process.ppid,
): RunContext {
  if (environment.TEST_WORKER_INDEX !== undefined) {
    const root = environment.PF_SETTINGS_RUN_ROOT;
    if (!root) throw new Error('Playwright worker is missing its owned run handoff');
    const context: RunContext = JSON.parse(readFileSync(join(root, 'run.json'), 'utf8'));
    if (context.launcherPid !== parentPid || context.runId !== environment.PF_SETTINGS_RUN_ID
      || realpathSync(context.root) !== realpathSync(root)) {
      throw new Error('Run handoff does not belong to this live Playwright launcher');
    }
    assertSourceIdentity(context.source, source);
    return context;
  }
  if (environment.PF_SETTINGS_RUN_ROOT || environment.PF_SETTINGS_RUN_ID) {
    throw new Error('PF_SETTINGS_RUN_ROOT/ID are internal worker handoffs; external run reuse is forbidden');
  }
  mkdirSync(parent, { recursive: true });
  const root = realpathSync(mkdtempSync(join(parent, 'pf-settings-e2e-')));
  const context = { root, runId: randomUUID(), launcherPid: pid, source };
  writeFileSync(join(root, 'run.json'), JSON.stringify(context, null, 2), { flag: 'wx' });
  environment.PF_SETTINGS_RUN_ROOT = root;
  environment.PF_SETTINGS_RUN_ID = context.runId;
  return context;
}
