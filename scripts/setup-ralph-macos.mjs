#!/usr/bin/env node
import { execFile } from 'node:child_process';
import { createHash } from 'node:crypto';
import { constants } from 'node:fs';
import { lstat, mkdir, open, readFile, readdir, realpath } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createInterface } from 'node:readline/promises';
import { pathToFileURL } from 'node:url';
import { isDeepStrictEqual, promisify } from 'node:util';

const exec = promisify(execFile);
const repository = 'OlyForge3D/PrintFarmer';
const origin = `https://github.com/${repository}.git`;
const policyDirectory = '.copilot/skills/ralph-loop';
const policyPaths = [
  policyDirectory, '.github/copilot-instructions.md', '.squad/config.json',
  'scripts/ci/ralph-*.mjs', 'scripts/ci/verify-squad-verdict.mjs',
];
const resolverPaths = ['scripts/ci/resolve-ios-simulator.sh', 'scripts/common-utils.sh'];
const shaPattern = /^[0-9a-f]{40}$/;
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export const help = `Usage: node setup-ralph-macos.mjs [--dry-run | --apply] [--clone]
  --repo ABSOLUTE_PATH           Existing checkout (read-only), or new clone path
  --host-config ABSOLUTE_JSON    Private output file outside every checkout
  --project-id UUID              Actual destination Copilot project ID
  --workflow-id UUID             Actual destination DISABLED workflow ID
  --app-host-id ID               Actual destination native environment ID
  --worktree-root ABSOLUTE_PATH  Destination app's session-worktree parent
  --cache-dir ABSOLUTE_PATH      New/empty cold observation-cache directory
  --github-login LOGIN           Expected authenticated GitHub account
  --approved-policy FULL_SHA     Optional exact candidate override (advanced)
  --renew-policy                 Explicitly review a policy approval renewal
  --previous-approval ABS_JSON    Read old approval; stage renewal in a NEW directory
  --previous-host-config ABS_JSON Preserve native bindings/control/history on renewal
  --role coordinator|consumer   Stage the native mailbox role package (all five
  --worker-id ID                 role options required; coordinator lives on Mac)
  --worker-registry ABS_JSON    Private approved worker/writer registry
  --control-repo OWNER/REPO      Existing PRIVATE control repository, read-only probe
  --mailbox-ref heads/NAME       Explicit approved mailbox ref, never auto-created

Default: read-only validation; no fetch, files, installs or app mutations.
Without a saved approval, discover exact development policy and show its scope.
--apply asks you to type "approve" in an interactive terminal before saving the
pin in private policy-approval.json alongside the four unverified staging files.
There is no --yes or headless first-approval bypass, including with an explicit pin.
Later runs reuse approval only while controlled policy content remains unchanged.
Renew with --renew-policy --previous-approval /old/policy-approval.json and a NEW
--host-config directory. Existing approval/packages are never overwritten.
--clone permits cloning ONLY with --apply into a nonexistent destination, using
the fixed trusted GitHub origin. Dry-run never clones and reports what is pending.
No option marks the host verified or enables/runs Ralph or Reaper.
Prerequisites: macOS, Node >=20, Git, authenticated gh, Copilot CLI + signed-in
Copilot app (app verification is manual), Python 3, selected Xcode >=26 and the
repository-approved iPhone/iPad simulators. No software is installed for you.
Native --role consumer also supports Windows with existing checkout and private
ACL-verified output parents, without Xcode/Python probes or automatic cloning.
Native role staging requires the existing PRIVATE control repo and registry;
it never initializes queue contents, invents genesis or enables the workflow.
Apple Silicon /opt/homebrew/bin, /usr/local/bin and ~/.local/bin are PATH fallbacks.
First discover/create destination project and DISABLED workflow through native
Copilot tools; see docs/ralph-macos-migration.md for the copy-paste discovery prompt.
`;

function absolute(value, label) {
  if (!value || !path.isAbsolute(value) || /[\x00-\x1f\x7f]/.test(value) ||
      value.split(/[\\/]/).some((part) => part === '..' || part === '.') ||
      path.normalize(value) !== value || value === path.parse(value).root || value === os.homedir()) {
    throw new Error(`${label} must be a normalized absolute non-root path without traversal or control characters.`);
  }
  return value;
}

export function parseArgs(args) {
  const options = {};
  const flags = new Set(['apply', 'dry-run', 'clone', 'renew-policy']);
  const values = new Set([
    'repo', 'host-config', 'project-id', 'workflow-id', 'app-host-id',
    'worktree-root', 'cache-dir', 'github-login', 'approved-policy', 'previous-approval',
    'role', 'worker-id', 'worker-registry', 'control-repo', 'mailbox-ref', 'previous-host-config',
  ]);
  for (let index = 0; index < args.length; index++) {
    const name = args[index].replace(/^--/, '');
    if (args[index] === '--help') return { help: true };
    if (!args[index].startsWith('--') || (!flags.has(name) && !values.has(name)) || name in options) {
      throw new Error('Unknown or duplicate option; use --help.');
    }
    if (flags.has(name)) options[name] = true;
    else {
      const value = args[++index];
      if (!value || value.startsWith('--')) throw new Error(`Missing value for --${name}.`);
      options[name] = value;
    }
  }
  if (options.apply && options['dry-run']) throw new Error('Choose --apply or --dry-run, not both.');
  for (const name of values) {
    if (!['approved-policy', 'previous-approval', 'previous-host-config', 'role', 'worker-id', 'worker-registry', 'control-repo', 'mailbox-ref'].includes(name) && !options[name]) throw new Error(`Missing --${name}; use --help.`);
  }
  for (const name of ['repo', 'host-config', 'worktree-root', 'cache-dir']) absolute(options[name], name);
  for (const name of ['project-id', 'workflow-id']) {
    if (!uuidPattern.test(options[name])) throw new Error(`--${name} must be an actual native UUID.`);
  }
  if (!/^[a-zA-Z0-9][a-zA-Z0-9._:-]{0,127}$/.test(options['app-host-id'])) {
    throw new Error('--app-host-id must be a native environment identifier.');
  }
  if (!/^[a-zA-Z0-9][a-zA-Z0-9-]{0,38}$/.test(options['github-login'])) throw new Error('Invalid --github-login.');
  if (options['approved-policy'] && !shaPattern.test(options['approved-policy'])) throw new Error('--approved-policy requires one full lowercase Git commit SHA.');
  if (Boolean(options['renew-policy']) !== Boolean(options['previous-approval'])) {
    throw new Error('Renewal requires both --renew-policy and --previous-approval, with --host-config in a NEW private directory.');
  }
  if (options['previous-approval']) absolute(options['previous-approval'], 'Previous approval');
  if (options['previous-host-config']) {
    absolute(options['previous-host-config'], 'Previous host config');
    if (!options['renew-policy'] || !options.role ||
        path.dirname(options['previous-host-config']) !== path.dirname(options['previous-approval']) ||
        path.dirname(options['previous-host-config']) === path.dirname(options['host-config'])) {
      throw new Error('Previous host config requires native policy renewal, its adjacent old approval, and a NEW output directory.');
    }
  }
  if (['role', 'worker-id', 'worker-registry', 'control-repo', 'mailbox-ref'].some((key) => options[key])) {
    if (!['role', 'worker-id', 'worker-registry', 'control-repo', 'mailbox-ref'].every((key) => options[key]) ||
        !['coordinator', 'consumer'].includes(options.role) ||
        !/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/.test(options['worker-id']) ||
        !/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(options['control-repo']) ||
        options['control-repo'].toLowerCase() === repository.toLowerCase() ||
        !/^heads\/[A-Za-z0-9_-][A-Za-z0-9_/-]*$/.test(options['mailbox-ref']) ||
        options['mailbox-ref'].includes('//') || options['mailbox-ref'].endsWith('/')) throw new Error('Invalid/incomplete native-role package options.');
    absolute(options['worker-registry'], 'Worker registry');
  }
  return options;
}

async function statIfExists(target) {
  try { return await lstat(target); }
  catch (error) {
    if (error.code === 'ENOENT') return undefined;
    throw error;
  }
}

export async function checkPath(target, kind = 'directory') {
  absolute(target, 'Path');
  let current = path.parse(target).root;
  const parts = target.slice(current.length).split(path.sep).filter(Boolean);
  for (let index = 0; index < parts.length; index++) {
    current = path.join(current, parts[index]);
    const stat = await statIfExists(current);
    if (!stat) continue;
    if (stat.isSymbolicLink()) throw new Error(`Symlink target/ancestor refused: ${current}`);
    const file = index === parts.length - 1 && kind === 'file';
    if (file ? !stat.isFile() || stat.nlink !== 1 : !stat.isDirectory()) {
      throw new Error(`Unsafe ${kind} target: ${current}`);
    }
  }
}

function overlaps(left, right) {
  return left === right || left.startsWith(`${right}${path.sep}`) || right.startsWith(`${left}${path.sep}`);
}

async function outsideCheckout(target) {
  for (let current = target; current !== path.parse(current).root; current = path.dirname(current)) {
    if (await statIfExists(path.join(current, '.git'))) throw new Error(`Private output must be outside all checkouts: ${target}`);
  }
}

function shellQuote(value) {
  return process.platform === 'win32' ? `'${value.replaceAll("'", "''")}'` : `'${value.replaceAll("'", "'\\''")}'`;
}

export async function runCommand(tool, args, { cwd, env = {} } = {}) {
  const environment = {
    ...process.env,
    PATH: process.platform === 'win32' ? process.env.PATH : `${process.env.PATH ?? ''}:/opt/homebrew/bin:/usr/local/bin:${os.homedir()}/.local/bin`,
    GIT_OPTIONAL_LOCKS: '0', GIT_TERMINAL_PROMPT: '0', GH_PROMPT_DISABLED: '1',
    ...env,
  };
  delete environment.GITHUB_ENV;
  delete environment.BASH_ENV;
  delete environment.ENV;
  try {
    return (await exec(tool, args, { cwd, env: environment, shell: false, encoding: 'utf8', timeout: 120_000, maxBuffer: 16 * 1024 * 1024 })).stdout.trim();
  } catch (error) {
    // CLI errors can contain remote URLs or authentication material.
    throw new Error(`${tool} failed (${error.code === 'ENOENT' ? 'not installed/on PATH' : 'nonzero exit or timeout'}). Raw output suppressed.`);
  }
}

function isControlled(file) {
  return file === policyDirectory || file.startsWith(`${policyDirectory}/`) ||
    ['.github/copilot-instructions.md', '.squad/config.json', 'scripts/ci/verify-squad-verdict.mjs', ...resolverPaths].includes(file) ||
    /^scripts\/ci\/ralph-.*\.mjs$/.test(file);
}

function manifestFromFiles(files) {
  files.sort((left, right) => left.path < right.path ? -1 : left.path > right.path ? 1 : 0);
  if (!files.length) throw new Error('Approved controlled policy tree is empty.');
  return { files, digest: createHash('sha256').update(JSON.stringify(files)).digest('hex') };
}

async function developmentHead(command) {
  const head = JSON.parse(await command('gh', ['api', '--hostname', 'github.com', `repos/${repository}/commits/development`]));
  if (!shaPattern.test(head.sha ?? '')) throw new Error('GitHub did not return an exact development SHA.');
  return head.sha;
}

async function policyManifest(repo, commit, command) {
  const listing = await command('git', ['ls-tree', '-r', '-z', '--full-tree', commit], { cwd: repo });
  const files = listing.split('\0').filter(Boolean).map((entry) => {
    const match = entry.match(/^([0-7]{6}) (blob|tree|commit) ([0-9a-f]{40})\t([\s\S]+)$/);
    if (!match) throw new Error('Invalid controlled Git tree entry.');
    return { mode: match[1], sha: match[3], path: match[4] };
  }).filter((file) => isControlled(file.path));
  return manifestFromFiles(files);
}

async function readApproval(file) {
  await checkPath(file, 'file');
  await outsideCheckout(path.dirname(file));
  if (!await statIfExists(file)) return undefined;
  const content = await readFile(file, 'utf8');
  await checkOutputs(new Map([[file, content]]));
  let record;
  try { record = JSON.parse(content); }
  catch { throw new Error('Invalid policy-approval.json; retain it and investigate, never silently replace it.'); }
  if (record?.schemaVersion !== 1 || record.repository !== repository || record.baseBranch !== 'development' ||
      !shaPattern.test(record.approvedCommit ?? '') || !/^[0-9a-f]{64}$/.test(record.controlledContentSha256 ?? '') ||
      !Number.isFinite(Date.parse(record.approvedAt)) ||
      JSON.stringify(record.controlledPaths) !== JSON.stringify([...policyPaths, ...resolverPaths])) {
    throw new Error('Policy approval receipt has invalid identity, scope or content metadata.');
  }
  return { record, content };
}

export async function confirmPolicy(summary) {
  process.stderr.write(`Policy approval review:\n${JSON.stringify(summary, undefined, 2)}\n`);
  if (!process.stdin.isTTY || !process.stderr.isTTY) {
    throw new Error('New policy approval requires an interactive terminal. Run --apply yourself after reviewing dry-run output; headless input cannot approve policy.');
  }
  const terminal = createInterface({ input: process.stdin, output: process.stderr });
  try {
    return (await terminal.question('Approve ONLY this exact policy (not host attestation or activation)? Type approve; anything else refuses: ')).trim() === 'approve';
  } finally { terminal.close(); }
}

export async function validatePolicy(options, command, development) {
  const cwd = options.repo;
  const git = (args) => command('git', args, { cwd });
  const api = async (endpoint) => JSON.parse(await command('gh', ['api', '--hostname', 'github.com', `repos/${repository}/${endpoint}`]));
  const approved = options['approved-policy'];
  if (typeof approved !== 'string' || approved.length !== 40 || !shaPattern.test(approved)) throw new Error('Policy subprocess inputs require a full lowercase Git commit SHA.');
  const actualOrigin = await git(['remote', 'get-url', 'origin']);
  if (![origin, origin.slice(0, -4), `git@github.com:${repository}.git`, `git@github.com:${repository}`].includes(actualOrigin)) {
    throw new Error('Wrong origin: require exactly OlyForge3D/PrintFarmer on github.com (HTTPS or git@ SSH).');
  }
  if (await realpath(await git(['rev-parse', '--show-toplevel'])) !== await realpath(cwd)) {
    throw new Error('--repo must be the checkout root.');
  }
  await git(['cat-file', '-e', `${approved}^{commit}`]);
  await git(['merge-base', '--is-ancestor', approved, 'HEAD']);
  const flags = await git(['ls-files', '-v', '--', ...policyPaths, ...resolverPaths]);
  if (flags.split('\n').some((line) => /^[a-zS] /.test(line))) {
    throw new Error('Policy/resolver index entries use assume-unchanged or skip-worktree flags; remove those flags explicitly before validation.');
  }
  await git(['diff', '--quiet', '--no-ext-diff', '--no-textconv', approved, '--', ...policyPaths, ...resolverPaths]);
  const untracked = await git(['ls-files', '--others', '--', ...policyPaths, ...resolverPaths]);
  if (untracked) throw new Error('Untracked (including ignored) policy/resolver files are not trusted.');
  const head = development ?? await developmentHead(command);
  const comparison = await api(`compare/${approved}...${head}`);
  if (!['ahead', 'identical'].includes(comparison.status) || comparison.merge_base_commit?.sha !== approved) {
    throw new Error('Approved policy is not an ancestor of live development.');
  }
  const manifest = await policyManifest(cwd, approved, command);
  const commit = await api(`git/commits/${head}`);
  if (commit.sha !== head || !shaPattern.test(commit.tree?.sha ?? '')) throw new Error('Invalid immutable development tree identity.');
  const tree = await api(`git/trees/${commit.tree.sha}?recursive=1`);
  if (tree.sha !== commit.tree.sha || tree.truncated !== false || !Array.isArray(tree.tree)) {
    throw new Error('Remote policy tree coverage is incomplete; approval cannot be inferred.');
  }
  const paths = new Set();
  const remoteFiles = tree.tree.filter((entry) => {
    if (typeof entry.path !== 'string' || !['blob', 'tree', 'commit'].includes(entry.type) ||
        !/^[0-7]{6}$/.test(entry.mode ?? '') || !shaPattern.test(entry.sha ?? '') || paths.has(entry.path)) {
      throw new Error('Remote policy tree contains malformed or duplicate entries.');
    }
    paths.add(entry.path);
    return entry.type !== 'tree' && isControlled(entry.path);
  }).map(({ mode, sha, path: name }) => ({ mode, sha, path: name }));
  if (manifestFromFiles(remoteFiles).digest !== manifest.digest) throw new Error('Live development controlled policy/resolver content changed; saved approval cannot cover it.');
  const policy = JSON.parse(await git(['show', `${approved}:${policyDirectory}/hosts.json`]));
  const profile = policy?.hosts?.['macos-mobile'];
  if (policy.schemaVersion !== 1 || policy.repository !== repository || policy.baseBranch !== 'development' ||
      !policy.policyVersion || profile?.configured !== true || profile.platform !== 'darwin' ||
      profile.scope !== 'mixed' || profile.admission !== 'native-local' ||
      profile.maxLocalXcodeJobs !== 1 || profile.maxLocalSessions !== 5 ||
      profile.maxMobileSessions !== 1 || profile.maxGeneralSessions !== 4 ||
      profile.generalAdmission !== 'blocked-pending-shared-authority') {
    throw new Error('Approved policy must preserve macOS 1 mobile + 4 general (5 total), one Xcode job and the unresolved shared-authority gate.');
  }
  const windowsProfile = policy.hosts?.['windows-general'];
  if (windowsProfile?.configured !== true || windowsProfile.platform !== 'win32' ||
      windowsProfile.scope !== 'general' || windowsProfile.admission !== 'windows-shared' ||
      windowsProfile.maxLocalSessions !== 5 || windowsProfile.maxMobileSessions !== 0 ||
      windowsProfile.maxGeneralSessions !== 5 || windowsProfile.newRemoteMobileDispatch !== false) {
    throw new Error('Approved policy must preserve Windows 0 mobile + 5 general and prohibit new remote mobile dispatch.');
  }
  if (Object.values(policy.hosts).some((host) =>
    ['workflowId', 'projectId', 'appHostId', 'worktreeRoot', 'cacheDirectory'].some((key) => key in host))) {
    throw new Error('Approved policy still embeds machine deployment bindings. Use the reviewed merged role-only policy, not a source-host pin.');
  }
  const template = await git(['show', `${approved}:${policyDirectory}/bootstrap.md`]);
  const prompt = template.match(/```text\n([\s\S]*?)\n```/)?.[1];
  if (!prompt || !prompt.includes('--approved-policy POLICY_COMMIT') ||
      !['<profile-id>', '<exact-workflow-id>', '<approved-absolute-private-json-path>', '<approved-full-SHA>'].every((token) => prompt.includes(token))) {
    throw new Error('Approved bootstrap template is unsupported; review the updated template rather than inventing a fallback.');
  }
  let rolePrompt;
  if (options.role) {
    if (!prompt.includes('Read .copilot/skills/ralph-loop/automation.md')) throw new Error('Approved bootstrap lacks the exact role-routing boundary.');
    rolePrompt = await git(['show', `${approved}:${policyDirectory}/native-roles.md`]);
    if (!rolePrompt.includes('NATIVE-MAILBOX-ROLE-V2')) throw new Error('Approved policy lacks the supported local-owner native role contract; renew the policy.');
    for (const file of ['ralph-mailbox.mjs', 'ralph-native-runtime.mjs']) {
      await git(['cat-file', '-e', `${approved}:scripts/ci/${file}`]);
    }
  }
  return { profile, policyVersion: policy.policyVersion, prompt, rolePrompt, development: head, manifest };
}

function artifacts(options, policy, deployment, previous) {
  const workflow = options['workflow-id'];
  const approved = options['approved-policy'];
  const config = {
    host: deployment?.worker.host ?? 'macos-mobile', workflowId: workflow, appHostId: options['app-host-id'],
    projectId: options['project-id'], worktreeRoot: options['worktree-root'],
    cacheDirectory: options['cache-dir'], verified: false,
  };
  if (deployment) Object.assign(config, {
    role: options.role, workerId: options['worker-id'], control: deployment.control,
    stateDirectory: path.join(path.dirname(options['host-config']), 'native-state'),
    approvedPolicy: approved, migrationAttested: false,
    executionTrust: 'local-owner-v1',
    automationWorkflowIds: [workflow],
  });
  if (previous) {
    for (const key of ['host', 'role', 'workerId', 'workflowId', 'projectId', 'appHostId', 'worktreeRoot']) {
      if (previous[key] !== config[key]) throw new Error(`Renewal cannot change ${key}; reconcile deployment changes separately.`);
    }
    const { genesisSha, ...oldControl } = previous.control;
    if (!isDeepStrictEqual(oldControl, deployment.control)) throw new Error('Renewal cannot change control authority, registry or repository identity.');
    if (genesisSha !== undefined && !shaPattern.test(genesisSha)) throw new Error('Invalid existing genesis; reconcile without replacing it.');
    config.control = previous.control;
    config.stateDirectory = absolute(previous.stateDirectory, 'Existing native state directory');
    config.automationWorkflowIds = previous.automationWorkflowIds;
    config.migrationAttested = previous.migrationAttested === true;
  }
  const quote = config.host === 'windows-general' ? (value) => `'${value.replaceAll("'", "''")}'` : shellQuote;
  const command = `node scripts/ci/ralph-automation.mjs preflight --host ${config.host} --workflow ${quote(workflow)} --host-config ${quote(options['host-config'])} --approved-policy ${approved}`;
  const bootstrap = policy.prompt
    .replace('<profile-id>', config.host).replace('<exact-workflow-id>', workflow)
    .replace('<approved-absolute-private-json-path>', JSON.stringify(options['host-config']))
    .replace('<approved-full-SHA>', approved)
    .replace(/Run node scripts\/ci\/ralph-automation\.mjs preflight --host HOST --workflow WORKFLOW\n--host-config HOST_CONFIG --approved-policy POLICY_COMMIT\./, `Run this exact command:\n${command}`);
  if (bootstrap.includes('--approved-policy POLICY_COMMIT') || /<[^>]+>/.test(bootstrap)) {
    throw new Error('Bootstrap template substitution failed; no files written.');
  }
  const prompt = deployment ? `NATIVE-MAILBOX-ROLE-V2
Run exactly one ${options.role} round, then exit. Private host config: ${JSON.stringify(options['host-config'])}.
Approved outer/preflight policy commit: ${approved}. Worker ID: ${options['worker-id']}.
Do NOT follow the legacy host dispatcher instructions. Apply these guards FIRST:
${bootstrap.split('Read .copilot/skills/ralph-loop/automation.md')[0]}
After those guards and non-authorizing preflight, follow ONLY
.copilot/skills/ralph-loop/native-roles.md, role ${options.role}.
All runtime commands use --host-config ${JSON.stringify(options['host-config'])}.
Workflow/project/environment IDs are owner-configured deployment assertions,
NOT independently authenticated current execution facts. Use local-owner-v1.
Acquire a fresh atomic begin-round token; the worktree path is NOT a round lock.
This workflow may remain disabled pending native attestation, pinned private
queue genesis and explicitly verified authority migration. No activation is
implied by this saved prompt. No SSH, CLI worker or remote app session creation.
` : bootstrap;
  const settings = previous ? {
    workflow_id: workflow, enabled: false, prompt,
  } : {
    name: deployment ? `Ralph ${options.role} - ${options['worker-id']} - PrintFarmer` : 'Ralph macOS - PrintFarmer', project_id: config.projectId,
    host_id: config.appHostId, workflow_id: workflow, enabled: false,
    workspace_type: 'worktree', mode: 'autopilot', model: 'gpt-5.6-luna',
    reasoning_effort: 'medium', interval: 'manual', cron_expression: '40 * * * *',
    prompt,
  };
  const handoff = deployment ? `NATIVE-MAILBOX-ROLE-V2 setup only. Do not run the workflow prompt.
Use supported native tools ON THIS DEVICE to verify and save workflow-settings.json
to the existing workflow ${workflow}, always enabled:false, and read it back.
Verify project/environment/worktree bindings from actual native app records.
Coordinator and mini consumer MUST have distinct workflow IDs and private packages.
Before first ready, natively verify BOTH mini workflow IDs and include both in
automationWorkflowIds in EACH mini package. Windows lists only its verified local
consumer. Never exempt ordinary work or supplied/unverified IDs from inventory.
Coordinator performs global triage and reservation; consumers only assigned work.
Control repository ${deployment.control.repository} numeric ID ${deployment.control.repositoryId}
was observed PRIVATE. No repo/ref/permission changes were made. Read current
.copilot/skills/ralph-loop/native-roles.md for the exact initialization, native
local-owner trust, local journal and reconciliation contracts.
The owner explicitly accepts shared-writer trust: any approved private repo writer
can technically impersonate a role. Do not describe this as independent authentication.
No signing keys. Keep credentials, native session IDs, paths and prompts OFF the queue.
Registry and bindings are deployment assertions pending explicit owner acceptance.
The app has NO supported in-session current-automation identity API. Do not invent
native.actual or env metadata. Explicitly accept executionTrust:local-owner-v1:
approved local code/config and the same local account/shared GitHub writers are
trusted. Filesystem checks establish context/isolation, not unique invocation.
The runtime atomically issues a one-time round token and persists its digest;
same-worktree competitors cannot reacquire it. Lost acquisition requires recovery.
After native bindings and all old authorities/Windows ledgers/workers are reconciled
with proven terminal or explicitly fenced handoff evidence, the owner may attest
migrationAttested:true and verified:true. These attestations are prerequisites for
initialization, not permission to write the queue. The owner must separately
authorize manual coordinator initialization from an approved isolated worktree,
using the supported terminal and runtime initialize request; no workflow run or
current-automation association is required. Follow native-roles.md's exact request.
Pin its returned genesis SHA in EVERY role's private control config before ordinary
rounds. Never initialize on an existing ref or retry uncertain init.
${previous ? 'RENEWAL: settings are a prompt-only disabled update to the SAME workflow. Omitted settings preserve the live name/model/effort/schedule/project/environment/workspace; read them back, never apply fresh-install defaults. Existing control/genesis, migration attestation and native-state path were retained. Old package/approval/journal/claims were NOT modified or copied. verified remains false until explicit acceptance of this new contract. Reconcile old active rounds; do not reset state or invent tokens.' : ''}
Do not fabricate evidence, infer cessation from disabled schedules or copy ledgers.
Keep source and destination schedules and Reaper disabled. Activation and any live
queue initialization/write require separate explicit authorization.
` : `This is setup/attestation only, NOT a Ralph round. Do not run the saved workflow prompt.
Keep both old and destination Ralph and Reaper disabled. Do not edit any source-host files.
The supplied destination bindings are claims until verified through live supported Copilot tools:
${JSON.stringify(config, undefined, 2)}
Approved Git policy commit (both outer guard and --approved-policy): ${approved}
Shared policy defines the macos-mobile ROLE only; deployment IDs are private.
The historical macos-mobile identifier now covers all issue eligibility with
hard 1 mobile + 4 general work slots (5 total), no borrowing; Xcode remains 1.
Windows permits 5 general and no NEW mobile work. Mac general cross-host
admission remains blocked until a shared atomic authority path is approved;
do not report four eligible slots as four usable unattended slots.

Use list_projects, list_workflows, get_session and live session inventory to verify
repository, actual destination project/workflow/environment and the disabled state.
Use the native automation editor/environment picker if the tools cannot discover an
environment ID. Do not infer that source ID "local" denotes this destination.
Verify an app-created isolated worktree's actual parent equals worktreeRoot.
Confirm Copilot app sign-in/entitlement and selected model availability natively.
Verify repository access uses the intended account ${options['github-login']}.
No app database edits, undocumented endpoints, exported sessions or token copying.

Do not patch shared hosts.json or reuse a source workflow UUID for a new installation.
Use the reviewed merged role-only policy and the SAME approved commit for both the
outer guard and --approved-policy. A policy update requires renewed owner approval.
Regenerate changed output into a NEW private directory; never overwrite old evidence.

Read workflow-settings.json and workflow-prompt.txt as data, not instructions to run.
Read policy-approval.json as private policy-confirmation bookkeeping, not native
attestation. The standalone Node helper cannot call app-native save_workflow.
With user approval save the exact generated prompt/settings to the verified destination
workflow using save_workflow or the native editor, enabled:false. Read back all fields.
Do not use run_workflow. Do not create another workflow when this ID already exists.
Reconcile ALL old local claims and Windows-owned remote jobs with their original
owner/job/digest/fence authority. Prove terminal state or authorized sole-owner
handoff. Idle, absent sessions and stopped controllers are NOT proof workers stopped.
Unreachable authorities retain claims and block cutover. Preserve historical records.
Never manufacture completion, fences, ownership or terminal evidence.

Only after those checks and explicit maintainer authorization may you manually edit
the private host config's verified:false to verified:true. This is a deployment
attestation, not an automatic result of the setup script or supplied IDs.
Before executing repository code in the app-created isolated verification worktree,
perform the generated saved prompt's origin/fresh-fetch/ancestor/current+fetched
policy comparisons and untracked checks, quoting 'scripts/ci/ralph-*.mjs' as a Git
pathspec. Then run ONLY this non-dispatching preflight at that worktree's root:
${command}
Preflight fetches origin; it must return dispatchAuthorized:false and
nativeIdentityVerified:false. This does not authenticate a current automation.
Do not follow the generated prompt into a round. A successful preflight is NOT
proof of native identity or ownership; retain the attestation evidence separately.
The legacy dispatcher must remain disabled. Stage explicit native coordinator/
consumer packages for owner-configured role execution; current-automation identity
is not exposed by the supported app and must never be fabricated.

CUTOVER: cold cache only; never copy session DBs, worktrees, ledgers or caches.
Keep old Ralph disabled and retain all historical state. Reaper remains disabled
and separate. Only after native-binding/ownership checks may the owner separately
authorize one controlled round and inspect real handoff receipts. Schedule enablement
requires a further explicit owner decision. No launchd jobs or recurring processes.
`;
  const directory = path.dirname(options['host-config']);
  return new Map([
    [options['host-config'], `${JSON.stringify(config, undefined, 2)}\n`],
    [path.join(directory, 'workflow-settings.json'), `${JSON.stringify(settings, undefined, 2)}\n`],
    [path.join(directory, 'workflow-prompt.txt'), `${prompt}\n`],
    [path.join(directory, 'app-native-handoff.txt'), handoff],
  ]);
}

async function makeDirectory(target) {
  if (target === path.parse(target).root || target === os.homedir()) return;
  await checkPath(target);
  if (await statIfExists(target)) return;
  await makeDirectory(path.dirname(target));
  await mkdir(target, { mode: 0o700 });
}

async function checkOutputs(files) {
  for (const [target, content] of files) {
    await checkPath(target, 'file');
    const parent = await statIfExists(path.dirname(target));
    if (process.platform === 'win32') {
      let privateParent = path.dirname(target);
      while (!await statIfExists(privateParent) && privateParent !== path.parse(privateParent).root) privateParent = path.dirname(privateParent);
      const script = `$ErrorActionPreference='Stop'; $s=[System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value; $a=Get-Acl -LiteralPath $env:RALPH_PRIVATE_CHECK_PATH; if($a.Owner -ne [System.Security.Principal.WindowsIdentity]::GetCurrent().Name){exit 1}; foreach($r in $a.Access){if($r.AccessControlType -eq 'Allow' -and $r.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value -notin @($s,'S-1-5-18','S-1-5-32-544')){exit 1}}; 'private'`;
      for (const privateTarget of [privateParent, ...(await statIfExists(target) ? [target] : [])]) {
        if (await runCommand('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], {
          env: { RALPH_PRIVATE_CHECK_PATH: privateTarget },
        }) !== 'private') throw new Error('Private Windows ACL verification failed.');
      }
    } else if (parent && (parent.uid !== process.getuid() || (parent.mode & 0o022) !== 0)) {
      throw new Error(`Private output directory must be owned by you and not group/world-writable: ${path.dirname(target)}`);
    }
    const stat = await statIfExists(target);
    if (process.platform !== 'win32' && stat && (stat.uid !== process.getuid() || (stat.mode & 0o077) !== 0)) {
      throw new Error(`Existing private output must be owned by you with mode 0600: ${target}`);
    }
    if (stat && await readFile(target, 'utf8') !== content) throw new Error(`Refusing to overwrite differing file: ${target}`);
  }
}

export async function setup(options, {
  command = runCommand, platform = process.platform, nodeVersion = process.versions.node,
  confirm = confirmPolicy,
} = {}) {
  if (platform !== 'darwin' && !(platform === 'win32' && options.role === 'consumer')) throw new Error('Run destination setup on macOS, or native consumer setup on Windows.');
  if (platform === 'win32' && options.clone) throw new Error('Register/clone the Windows project through supported native setup first; this helper will only read an existing Windows checkout.');
  if (options.role && options['renew-policy'] && !options['previous-host-config']) throw new Error('Native renewal requires --previous-host-config to preserve authority, bindings and native history.');
  if (Number(nodeVersion.split('.')[0]) < 20) throw new Error('Node >=20 is required; install it manually.');
  const directory = path.dirname(options['host-config']);
  const directories = [options.repo, options['worktree-root'], options['cache-dir'], directory];
  for (const target of directories) await checkPath(target);
  for (let index = 0; index < directories.length; index++) {
    for (const other of directories.slice(index + 1)) {
      if (overlaps(directories[index], other)) throw new Error('Repo, worktree parent, cold cache and private output directory must be disjoint.');
    }
  }
  for (const target of directories.slice(1)) await outsideCheckout(target);
  if (await statIfExists(options['cache-dir']) && (await readdir(options['cache-dir'])).length) {
    throw new Error('Cache must be new or empty. Choose a new cold-cache path; do not delete/copy an existing cache.');
  }
  await checkPath(options['host-config'], 'file');
  let previous, previousContent;
  if (options['previous-host-config']) {
    await checkPath(options['previous-host-config'], 'file');
    await outsideCheckout(path.dirname(options['previous-host-config']));
    previousContent = await readFile(options['previous-host-config'], 'utf8');
    await checkOutputs(new Map([[options['previous-host-config'], previousContent]]));
    previous = JSON.parse(previousContent);
    if (!previous.control || !previous.stateDirectory) throw new Error('Existing native package required for preservation-first renewal.');
    if (path.basename(previous.stateDirectory) !== 'native-state' ||
        directories.some((target) => overlaps(target, previous.stateDirectory))) {
      throw new Error('Retained native-state must be disjoint from the new package, checkout, worktrees and cold cache.');
    }
    await checkPath(previous.stateDirectory);
    await outsideCheckout(previous.stateDirectory);
  }
  const blockers = [];
  const probe = async (action, remediation) => {
    try { return await action(); }
    catch { blockers.push(remediation); return undefined; }
  };
  for (const [tool, args, remediation] of [
    ['git', ['--version'], 'Install Git/Xcode command-line tools manually, then expose git on PATH.'],
    ['gh', ['--version'], 'Install GitHub CLI manually, then expose gh on PATH.'],
    ['copilot', ['--version'], 'Install the supported Copilot CLI manually; open/sign in to the Copilot app separately.'],
    ...(platform === 'darwin' ? [['python3', ['--version'], 'Install Python 3 manually; the approved simulator resolver requires it.']] : []),
  ]) await probe(() => command(tool, args), remediation);
  await probe(() => command('gh', ['auth', 'status', '--hostname', 'github.com']),
    'Authenticate gh to github.com yourself (gh auth login); never copy source tokens.');
  await probe(async () => {
    const user = JSON.parse(await command('gh', ['api', '--hostname', 'github.com', 'user']));
    if (user.login?.toLowerCase() !== options['github-login'].toLowerCase()) throw new Error('Wrong account');
  }, 'Active gh account does not match --github-login; use gh auth switch/login yourself, then retry.');
  await probe(async () => {
    const repo = JSON.parse(await command('gh', ['api', '--hostname', 'github.com', `repos/${repository}`]));
    if (repo.full_name !== repository || repo.permissions?.push !== true) throw new Error('Wrong repository/access');
  }, 'Intended gh account must have write access to OlyForge3D/PrintFarmer; verify access with its administrator.');
  if (platform === 'darwin') {
    await probe(() => command('xcode-select', ['-p']), 'Select a full Xcode manually in Xcode Settings > Locations (do not use command-line-tools alone).');
    await probe(async () => {
    const version = await command('xcodebuild', ['-version']);
    if (Number(version.match(/^Xcode (\d+)/m)?.[1] ?? 0) < 26) throw new Error('Old Xcode');
    await command('xcrun', ['--find', 'swift']);
    await command('xcrun', ['swift', '--version']);
  }, 'Select Xcode >=26, complete first launch/license yourself, and confirm xcrun swift --version works.');
  }
  let deployment;
  if (options.role) {
    await checkPath(options['worker-registry'], 'file');
    const registry = JSON.parse(await readFile(options['worker-registry'], 'utf8'));
    if (registry?.version !== 1 || !Number.isSafeInteger(registry.epoch) || registry.epoch < 1 ||
        !/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/.test(registry.authorityId ?? '') ||
        !Array.isArray(registry.writers) ||
        registry.writers.some((name) => typeof name !== 'string' || !/^[A-Za-z0-9][A-Za-z0-9-]{0,38}$/.test(name)) ||
        !registry.writers.some((name) => name.toLowerCase() === options['github-login'].toLowerCase()) ||
        new Set(registry.writers.map((name) => name.toLowerCase())).size !== registry.writers.length ||
        !Array.isArray(registry.workers) || registry.workers.length !== 2 ||
        new Set(registry.workers.map((worker) => worker.workerId)).size !== 2 ||
        new Set(registry.workers.map((worker) => worker.host)).size !== 2 ||
        Object.keys(registry).some((key) => !['version', 'authorityId', 'epoch', 'writers', 'workers'].includes(key))) throw new Error('Invalid private worker registry.');
    for (const worker of registry.workers) {
      if (!['macos-mobile', 'windows-general'].includes(worker.host) ||
          !/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/.test(worker.workerId ?? '') ||
          !Array.isArray(worker.capabilities) || !worker.capabilities.length ||
          worker.capabilities.some((capability) => typeof capability !== 'string' || !/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/.test(capability)) ||
          new Set(worker.capabilities).size !== worker.capabilities.length ||
          Object.keys(worker).some((key) => !['workerId', 'host', 'capabilities'].includes(key))) throw new Error('Invalid worker role/capabilities; no deployment paths or secrets in registry.');
    }
    const worker = registry.workers.find((entry) => entry.workerId === options['worker-id']);
    if (!worker || worker.host !== (platform === 'darwin' ? 'macos-mobile' : 'windows-general')) throw new Error('Worker role must match this actual setup platform.');
    const controlRepository = options['control-repo'];
    if (typeof controlRepository !== 'string' || controlRepository.trim() !== controlRepository ||
        !/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(controlRepository) ||
        controlRepository.toLowerCase() === repository.toLowerCase()) throw new Error('Invalid private control repository API input.');
    const metadata = JSON.parse(await command('gh', ['api', '--hostname', 'github.com', `repos/${controlRepository}`]));
    if (metadata.full_name !== controlRepository || metadata.private !== true || metadata.visibility !== 'private' ||
        metadata.archived !== false || metadata.disabled !== false || metadata.permissions?.push !== true ||
        !Number.isSafeInteger(metadata.id) || metadata.id < 1) throw new Error('Control repository must be exact, PRIVATE, active and writable.');
    deployment = { worker, control: {
      repository: metadata.full_name, repositoryId: metadata.id, ref: options['mailbox-ref'],
      registry, sharedWriterTrustAccepted: true,
    } };
  }
  if (blockers.length) throw new Error(`Unmet prerequisites:\n- ${blockers.join('\n- ')}`);

  const exists = await statIfExists(options.repo);
  if (exists && options.clone) throw new Error('--clone requires a nonexistent destination; rerun without --clone to validate it.');
  if (!exists) {
    if (!options.clone) throw new Error('Repository missing. Use --clone explicitly or select an existing destination checkout.');
    if (!options.apply) return { status: 'clone-pending', message: `Dry-run: would clone ${origin}; policy/runtime/output validation requires the clone. No files written.` };
    await makeDirectory(path.dirname(options.repo));
    await command('git', ['-c', 'core.hooksPath=/dev/null', 'clone', '--template=', '--no-recurse-submodules',
      '--branch', 'development', '--', origin, options.repo]);
  }
  const approvalPath = path.join(directory, 'policy-approval.json');
  const previousPath = options['previous-approval'] ?? approvalPath;
  if (options['renew-policy'] && path.dirname(previousPath) === directory) {
    throw new Error('Renewal must use a NEW --host-config directory; existing approval and generated files are never overwritten.');
  }
  const saved = await readApproval(previousPath);
  if (options['renew-policy'] && !saved) throw new Error('--previous-approval does not exist; cannot review a renewal without its baseline.');
  if (options['renew-policy'] && await statIfExists(directory)) {
    throw new Error('Renewal requires a nonexistent output directory; preserve previous packages and choose a new path.');
  }
  if (saved && !options['renew-policy'] && options['approved-policy'] && options['approved-policy'] !== saved.record.approvedCommit) {
    throw new Error('Explicit pin differs from saved approval. Use --renew-policy with the previous approval and a NEW output directory.');
  }
  const development = await developmentHead(command);
  const approved = saved && !options['renew-policy'] ? saved.record.approvedCommit : options['approved-policy'] ?? development;
  options = { ...options, 'approved-policy': approved };
  let policy;
  try { policy = await validatePolicy(options, command, development); }
  catch (error) {
    throw new Error(`Policy validation blocked: ${error.message} The exact candidate must exist in a matching checkout; fetch/update your isolated checkout manually if needed, never reset dirty work. ${saved && !options['renew-policy'] ? 'Saved approval cannot be reused. For an actual controlled-policy revision, review with --renew-policy --previous-approval and a NEW --host-config directory; renewal cannot bypass failed trust checks.' : ''}`);
  }
  if (saved) {
    const previous = await policyManifest(options.repo, saved.record.approvedCommit, command);
    if (previous.digest !== saved.record.controlledContentSha256) throw new Error('Saved approval digest does not match its immutable Git commit.');
  }
  const needsApproval = !saved || Boolean(options['renew-policy']);
  const changes = saved ? await command('git', [
    'diff', '--no-ext-diff', '--no-textconv', '--stat', saved.record.approvedCommit, approved, '--', ...policyPaths, ...resolverPaths,
  ], { cwd: options.repo }) : 'Initial approval: all listed controlled files are in scope.';
  const review = {
    repository, baseBranch: 'development', exactCommit: approved,
    commitUrl: `https://github.com/${repository}/commit/${approved}`,
    observedDevelopment: development,
    previousApproval: saved?.record.approvedCommit,
    controlledContentSha256: policy.manifest.digest,
    controlledPaths: [...policyPaths, ...resolverPaths],
    controlledFiles: policy.manifest.files,
    changeSummary: changes.split('\n'),
    mobileRole: policy.profile,
    policyVersion: policy.policyVersion,
    ...(deployment ? { nativeRole: options.role, workerId: options['worker-id'], control: deployment.control } : {}),
    reviewCommand: saved
      ? `git -C ${shellQuote(options.repo)} diff --no-ext-diff --no-textconv ${saved.record.approvedCommit} ${approved} -- ${[...policyPaths, ...resolverPaths].map(shellQuote).join(' ')}`
      : `git -C ${shellQuote(options.repo)} show ${approved}:${policyDirectory}/automation.md`,
    authorizes: 'Only these immutable policy contents. NOT native identity attestation, cutover, a Ralph round or schedule activation.',
  };
  const files = artifacts(options, policy, deployment, previous);
  if (files.size !== 4 || files.has(approvalPath)) throw new Error('Host config filename conflicts with a generated handoff filename.');
  await checkOutputs(files);
  if (needsApproval && !options.apply) {
    return {
      status: 'approval-required', policyReview: review, nativeBindingsAttested: false,
      localPrerequisitesReady: false,
      pending: 'No writes or prompts. Simulator resolution awaits policy approval because it executes repository code. Rerun with --apply in an interactive terminal.',
    };
  }
  if (needsApproval && await confirm(review) !== true) throw new Error('Policy approval refused; no approval, config or handoff files written.');
  const ensureUnchanged = async () => {
    if (previous && await readFile(options['previous-host-config'], 'utf8') !== previousContent) throw new Error('Existing host config changed during renewal; refusing stale preservation.');
    if (deployment) {
      const metadata = JSON.parse(await command('gh', ['api', '--hostname', 'github.com', `repos/${deployment.control.repository}`]));
      if (metadata.id !== deployment.control.repositoryId || metadata.full_name !== deployment.control.repository ||
          metadata.private !== true || metadata.visibility !== 'private' || metadata.archived !== false ||
          metadata.disabled !== false || metadata.permissions?.push !== true ||
          JSON.stringify(JSON.parse(await readFile(options['worker-registry'], 'utf8'))) !== JSON.stringify(deployment.control.registry)) throw new Error('Private control repository or registry changed during approval.');
    }
    if (await developmentHead(command) !== development) {
      throw new Error('Development candidate changed during this run. Nothing is approved or staged automatically; rerun to review the new exact candidate.');
    }
    const checked = await validatePolicy(options, command, development);
    if (checked.manifest.digest !== policy.manifest.digest) throw new Error('Displayed policy content changed; approval cannot be transferred.');
    const currentReceipt = await readApproval(previousPath);
    if (currentReceipt?.content !== saved?.content) throw new Error('Approval receipt changed during this run; refusing concurrent replacement.');
  };
  await ensureUnchanged();
  const receipt = needsApproval ? {
    schemaVersion: 1, repository, baseBranch: 'development', approvedCommit: approved,
    controlledContentSha256: policy.manifest.digest, controlledPaths: [...policyPaths, ...resolverPaths],
    approvedAt: new Date().toISOString(), githubLogin: options['github-login'],
  } : saved.record;
  files.set(approvalPath, needsApproval ? `${JSON.stringify(receipt, undefined, 2)}\n` : saved.content);
  await checkOutputs(files);
  for (const family of platform === 'darwin' ? ['iPhone', 'iPad'] : []) {
    await probe(() => command('bash', ['scripts/ci/resolve-ios-simulator.sh', '--udid'], {
      cwd: options.repo, env: {
        IOS_SIMULATOR_DEVICE_FAMILY: family, IOS_SIMULATOR_DEVICE_PREFIX: `${family} `,
        IOS_SIMULATOR_DEVICE_PREFERENCE: '', IOS_SIMULATOR_RUNTIME_PREFERENCE: '',
      },
    }), `No approved ${family} simulator resolved. Run scripts/ci/resolve-ios-simulator.sh with IOS_SIMULATOR_DEVICE_FAMILY=${family} for exact approved runtime/build details. Install that runtime in Xcode Settings > Components and create an available device yourself; no boot/erase is needed.`);
  }
  if (blockers.length) throw new Error(`Unmet prerequisites:\n- ${blockers.join('\n- ')}`);
  if (options.apply) {
    await ensureUnchanged();
    for (const target of directories.slice(1)) await makeDirectory(target);
    await checkOutputs(files);
    for (const [target, content] of files) {
      if (await statIfExists(target)) {
        await checkOutputs(new Map([[target, content]]));
        continue;
      }
      const file = await open(target, constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | constants.O_NOFOLLOW, 0o600);
      try {
        await file.writeFile(content);
        await file.sync();
      }
      finally { await file.close(); }
    }
  }
  return {
    status: options.apply ? 'staged-unverified' : 'validated-not-attested',
    localPrerequisitesReady: true,
    nativeBindingsAttested: false,
    sharedPolicyRoleOnly: true,
    policyApproval: needsApproval ? 'explicitly-approved' : 'reused',
    approvedOuterAndPreflightCommit: options['approved-policy'],
    files: [...files.keys()], development: policy.development,
    activationBlockers: [
      'Native app bindings, sign-in/model availability and sole-owner/terminal handoff are not attested. Config remains verified:false; no preflight/round/schedule executed.',
      'Explicit local-owner-v1 acceptance is required. Configuration IDs are deployment assertions, not independent current-execution identity. Every round needs an atomically acquired token.',
      ...(deployment ? ['Before first ready, verify both mini coordinator/consumer workflow IDs natively and put both in automationWorkflowIds in each mini package; Windows lists only its verified local consumer. Unverified IDs cannot exempt sessions.'] : []),
      deployment
        ? 'Native role package staged only: pinned private queue genesis, reconciled legacy authority migration and local-owner acceptance are required. No queue writes or activation performed.'
        : 'Legacy package cannot activate the new coordinator/consumer architecture. Use --role with explicit private control repository and worker registry.',
    ],
  };
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try {
    const options = parseArgs(process.argv.slice(2));
    if (options.help) process.stdout.write(help);
    else process.stdout.write(`${JSON.stringify(await setup(options), undefined, 2)}\n`);
  } catch (error) {
    process.stderr.write(`Ralph destination setup blocked: ${error.message}\n`);
    process.exitCode = 1;
  }
}
