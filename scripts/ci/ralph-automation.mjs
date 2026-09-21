import { execFile } from 'node:child_process';
import { readFile, realpath } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { promisify } from 'node:util';
import { planPrRecovery } from './ralph-pr-recovery.mjs';

const exec = promisify(execFile);
const policyDirectory = '.copilot/skills/ralph-loop';
export const policyPaths = [
  policyDirectory, '.github/copilot-instructions.md', '.squad/config.json',
  'scripts/ci/ralph-automation.mjs', 'scripts/ci/ralph-pr-recovery.mjs',
  'scripts/ci/ralph-admission.mjs', 'scripts/ci/ralph-macos-ssh.mjs',
  'scripts/ci/ralph-round-cache.mjs', 'scripts/ci/ralph-github-snapshot.mjs',
  'scripts/ci/ralph-*.mjs', 'scripts/ci/verify-squad-verdict.mjs',
];

export function resolveAutomationHost(config, { host, workflow, runtime, platform = process.platform, cwd = process.cwd() }) {
  const profile = config?.hosts?.[host];
  if (config?.schemaVersion !== 1 || config.repository !== 'OlyForge3D/PrintFarmer' ||
      config.baseBranch !== 'development' || !config.policyVersion ||
      !profile || profile.configured !== true) {
    throw new Error(`Host ${host} is not configured: ${profile?.configurationBlocker ?? 'invalid or missing policy'}`);
  }
  if (profile.platform !== platform || profile.workflowId !== workflow ||
      !['mobile', 'general'].includes(profile.scope) ||
      !Number.isInteger(profile.maxLocalSessions) || profile.maxLocalSessions < 1 || profile.maxLocalSessions > 5) {
    throw new Error('Automation platform, workflow identity, scope or local capacity does not match the host profile.');
  }
  if (runtime?.host !== host || runtime.workflowId !== workflow || runtime.verified !== true ||
      !runtime.appHostId || !runtime.projectId) {
    throw new Error('A verified private host configuration matching this automation is required.');
  }
  const paths = platform === 'win32' ? path.win32 : path.posix;
  if (!paths.isAbsolute(runtime.worktreeRoot ?? '') || !paths.isAbsolute(runtime.cacheDirectory ?? '')) {
    throw new Error('Host paths must be verified absolute paths.');
  }
  const relative = paths.relative(runtime.worktreeRoot, cwd);
  if (!relative || relative.startsWith('..') || paths.isAbsolute(relative)) {
    throw new Error('Run only from an isolated automation worktree beneath the configured worktree root.');
  }
  return {
    ...profile, host, policyVersion: config.policyVersion,
    appHostId: runtime.appHostId, projectId: runtime.projectId,
    worktreeRoot: runtime.worktreeRoot, cacheDirectory: runtime.cacheDirectory,
  };
}

export async function verifyAutomationCheckout({ cwd, approvedPolicy, git = runGit }) {
  if (!/^[0-9a-f]{40}$/i.test(approvedPolicy ?? '')) throw new Error('An approved full policy commit is required.');
  const origin = await git(cwd, ['remote', 'get-url', 'origin']);
  if (!/^(?:https:\/\/github\.com\/|git@github\.com:)OlyForge3D\/PrintFarmer(?:\.git)?$/i.test(origin.trim())) {
    throw new Error('Automation origin must be OlyForge3D/PrintFarmer on github.com.');
  }
  const top = await git(cwd, ['rev-parse', '--show-toplevel']);
  if (await realpath(top.trim()) !== await realpath(cwd)) throw new Error('Run the bootstrap at the isolated worktree root.');
  await git(cwd, ['fetch', '--quiet', 'origin', 'development']);
  const fetchedBase = (await git(cwd, ['rev-parse', 'FETCH_HEAD'])).trim();
  if (!/^[0-9a-f]{40}$/i.test(fetchedBase)) throw new Error('Fresh development fetch did not produce an exact commit.');
  await git(cwd, ['merge-base', '--is-ancestor', approvedPolicy, fetchedBase]);
  await git(cwd, ['diff', '--exit-code', approvedPolicy, fetchedBase, '--', ...policyPaths]);
  await git(cwd, ['diff', '--exit-code', approvedPolicy, '--', ...policyPaths]);
  const untracked = await git(cwd, ['ls-files', '--others', '--exclude-standard', '--', ...policyPaths]);
  if (untracked.trim()) throw new Error('Untracked policy files are not approved automation inputs.');
}

async function runGit(cwd, args) {
  const { stdout } = await exec('git', args, { cwd, encoding: 'utf8', maxBuffer: 1024 * 1024 });
  return stdout;
}

export async function runAutomationPreflight({ host, workflow, hostConfig, approvedPolicy, cwd = process.cwd(), platform = process.platform }) {
  if (!path.isAbsolute(hostConfig ?? '')) throw new Error('--host-config must name the approved private absolute JSON path.');
  const config = JSON.parse(await readFile(path.join(cwd, policyDirectory, 'hosts.json'), 'utf8'));
  const runtime = JSON.parse(await readFile(hostConfig, 'utf8'));
  const profile = resolveAutomationHost(config, { host, workflow, runtime, platform, cwd });
  resolveAutomationHost(config, {
    host, workflow, platform, cwd: await realpath(cwd),
    runtime: { ...runtime, worktreeRoot: await realpath(runtime.worktreeRoot) },
  });
  await verifyAutomationCheckout({ cwd, approvedPolicy });
  return { profile, approvedPolicy, policy: `${policyDirectory}/automation.md`, dispatchAuthorized: false };
}

async function main() {
  const [command, ...args] = process.argv.slice(2);
  if (command === 'plan' && args.length === 0) {
    let input = '';
    for await (const chunk of process.stdin) {
      input += chunk;
      if (Buffer.byteLength(input) > 4 * 1024 * 1024) throw new Error('Recovery observations exceed 4 MiB; reduce unrelated payloads, never truncate coverage.');
    }
    process.stdout.write(`${JSON.stringify(planPrRecovery({ ...JSON.parse(input), now: Date.now() }))}\n`);
    return;
  }
  if (command !== 'preflight' || args.length !== 8 ||
      args[0] !== '--host' || args[2] !== '--workflow' || args[4] !== '--host-config' || args[6] !== '--approved-policy') {
    throw new Error('Usage: ralph-automation.mjs preflight --host ID --workflow UUID --host-config ABSOLUTE_JSON --approved-policy SHA | plan < observations.json');
  }
  const result = await runAutomationPreflight({ host: args[1], workflow: args[3], hostConfig: args[5], approvedPolicy: args[7] });
  process.stdout.write(`${JSON.stringify(result)}\n`);
}

if (process.argv[1] && fileURLToPath(import.meta.url) === fileURLToPath(pathToFileURL(process.argv[1]))) {
  main().catch((error) => {
    process.stderr.write(`Ralph automation blocked: ${error.message}\n`);
    process.exitCode = 1;
  });
}
