import { execFile } from 'node:child_process';
import { readFile, realpath } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { promisify } from 'node:util';
import { planPrRecovery } from './ralph-pr-recovery.mjs';
import { assessHostCapacity } from './ralph-host-capacity.mjs';

const exec = promisify(execFile);
const policyDirectory = '.copilot/skills/ralph-loop';
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const appHostPattern = /^[a-zA-Z0-9][a-zA-Z0-9._:-]{0,127}$/;
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
  if (['workflowId', 'projectId', 'appHostId', 'worktreeRoot', 'cacheDirectory'].some((key) => key in profile)) {
    throw new Error('Machine deployment bindings belong in private host configuration, not the shared host profile.');
  }
  if (profile.platform !== platform || !uuidPattern.test(workflow ?? '') ||
      !['mixed', 'general'].includes(profile.scope) || profile.maxLocalSessions !== 5 ||
      (host === 'macos-mobile' && (profile.scope !== 'mixed' || profile.maxMobileSessions !== 1 ||
        profile.maxGeneralSessions !== 4 || profile.maxLocalXcodeJobs !== 1 ||
        profile.generalAdmission !== 'blocked-pending-shared-authority')) ||
      (host === 'windows-general' && (profile.scope !== 'general' || profile.maxMobileSessions !== 0 ||
        profile.maxGeneralSessions !== 5 || profile.newRemoteMobileDispatch !== false))) {
    throw new Error('Automation platform, workflow identity, scope or local capacity does not match the host profile.');
  }
  if (runtime?.host !== host || runtime.workflowId !== workflow || runtime.verified !== true ||
      !appHostPattern.test(runtime.appHostId ?? '') || !uuidPattern.test(runtime.projectId ?? '')) {
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
    ...profile, host, policyVersion: config.policyVersion, workflowId: runtime.workflowId,
    appHostId: runtime.appHostId, projectId: runtime.projectId,
    worktreeRoot: runtime.worktreeRoot, cacheDirectory: runtime.cacheDirectory,
  };
}

export function compareNativeAutomationIdentity({ expected, actual, now = Date.now() }) {
  const execution = actual?.execution;
  const workflow = actual?.workflow;
  const project = actual?.project;
  const observed = Date.parse(actual?.observedAt);
  if (!Number.isFinite(observed) || observed > now || now - observed > 60_000 ||
      !uuidPattern.test(expected?.workflowId ?? '') || !uuidPattern.test(expected?.projectId ?? '') ||
      !appHostPattern.test(expected?.appHostId ?? '') || !expected?.worktreePath ||
      !uuidPattern.test(execution?.sessionId ?? '')) {
    throw new Error('Fresh native observations of the CURRENT executing automation are required; supplied configuration is not execution identity.');
  }
  if (execution.workflowId !== expected.workflowId || execution.projectId !== expected.projectId ||
      execution.appHostId !== expected.appHostId || execution.worktreePath !== expected.worktreePath ||
      workflow?.id !== expected.workflowId || workflow.projectId !== expected.projectId ||
      workflow.appHostId !== expected.appHostId || project?.id !== expected.projectId ||
      project.repository !== 'OlyForge3D/PrintFarmer') {
    throw new Error('Current native execution, workflow, project and private deployment bindings do not match.');
  }
  return { bindingsMatch: true, dispatchAuthorized: false, observationAuthentication: 'controller-native-tools-required' };
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
  const flags = await git(cwd, ['ls-files', '-v', '--', ...policyPaths]);
  if (flags.split('\n').some((line) => /^[a-zS] /.test(line))) throw new Error('Controlled policy uses unsafe index suppression flags.');
  await git(cwd, ['diff', '--exit-code', approvedPolicy, '--', ...policyPaths]);
  const untracked = await git(cwd, ['ls-files', '--others', '--', ...policyPaths]);
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
  return {
    profile, approvedPolicy, policy: `${policyDirectory}/automation.md`, dispatchAuthorized: false,
    nativeIdentityVerified: false,
    expectedNativeIdentity: {
      workflowId: profile.workflowId, projectId: profile.projectId,
      appHostId: profile.appHostId, worktreePath: await realpath(cwd),
    },
    nextStep: 'Reconcile the CURRENT native executing automation, workflow and project through supported app tools, then identity-check. Unknown execution identity blocks all round mutations.',
  };
}

async function main() {
  const [command, ...args] = process.argv.slice(2);
  if (['plan', 'identity-check', 'capacity-check'].includes(command) && args.length === 0) {
    let input = '';
    for await (const chunk of process.stdin) {
      input += chunk;
      if (Buffer.byteLength(input) > 4 * 1024 * 1024) throw new Error('Recovery observations exceed 4 MiB; reduce unrelated payloads, never truncate coverage.');
    }
    const data = { ...JSON.parse(input), now: Date.now() };
    const result = command === 'plan' ? planPrRecovery(data)
      : command === 'capacity-check' ? assessHostCapacity(data) : compareNativeAutomationIdentity(data);
    process.stdout.write(`${JSON.stringify(result)}\n`);
    return;
  }
  if (command !== 'preflight' || args.length !== 8 ||
      args[0] !== '--host' || args[2] !== '--workflow' || args[4] !== '--host-config' || args[6] !== '--approved-policy') {
    throw new Error('Usage: ralph-automation.mjs preflight --host ID --workflow UUID --host-config ABSOLUTE_JSON --approved-policy SHA | plan < observations.json | identity-check < native-observations.json | capacity-check < capacity-observations.json');
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
