import { createHash, randomUUID } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, realpathSync, writeFileSync } from 'node:fs';
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

export function sourceIdentity(verifyCommitted = false): SourceIdentity {
  const git = (...args: string[]) => execFileSync('git', args, { cwd: repo, encoding: 'utf8' }).trim();
  const sourceSha = git('rev-parse', 'HEAD');
  if (!/^[a-f\d]{40}$/.test(sourceSha)) throw new Error('Cannot establish a full source revision');
  const hashes = inputs.map(path => {
    const content = readFileSync(resolve(repo, path), 'utf8');
    if (verifyCommitted && hash(content) !== hash(execFileSync('git', ['show', `HEAD:${path}`], { cwd: repo, encoding: 'utf8' }))) {
      throw new Error(`${path}: uncommitted input cannot produce reviewed-head evidence`);
    }
    return [path, hash(content)];
  });
  if (verifyCommitted && git('status', '--porcelain', '--untracked-files=no') !== '') {
    throw new Error('Commit or reconcile tracked source changes before live evidence generation');
  }
  return {
    schema: 1,
    sourceSha,
    harnessSha256: hash(JSON.stringify(hashes)),
    registrySha256: hashes.at(-1)![1],
  };
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
