import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import yaml from 'js-yaml';
import { approvedSdkVersion, repositorySdkFiles, sdkPolicyErrors } from '../check-dotnet-sdk.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const python = process.platform === 'win32' ? 'python' : 'python3';
const sdk = { version: '10.0.402', rollForward: 'disable', allowPrerelease: false };
const goodWorkflow = `jobs:
  build:
    steps:
      - uses: actions/setup-dotnet@v6
        with:
          global-json-file: global.json
      - run: python3 scripts/dotnet-sdk.py verify
`;
const goodDockerfile = `ARG SDK_TAG
FROM mcr.microsoft.com/dotnet/sdk:\${SDK_TAG} AS build
COPY global.json ./
RUN dotnet --version
FROM mcr.microsoft.com/dotnet/aspnet:10.0
`;

function fixture() {
  return new Map([
    ['global.json', JSON.stringify({ sdk })],
    ['.github/workflows/build.yml', goodWorkflow],
    ['.github/dependabot.yml', 'updates:\n  - package-ecosystem: dotnet-sdk\n    directory: "/"'],
    ['scripts/docker/dockerfiles/Dockerfile', goodDockerfile],
  ]);
}

function tempRepo(t) {
  const directory = mkdtempSync(join(tmpdir(), 'printfarmer-sdk-'));
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  mkdirSync(join(directory, 'scripts'), { recursive: true });
  copyFileSync(join(root, 'scripts/dotnet-sdk.py'), join(directory, 'scripts/dotnet-sdk.py'));
  writeFileSync(join(directory, 'global.json'), JSON.stringify({ sdk }));
  return directory;
}

test('single exact SDK, independent runtime and target-framework versions are accepted', () => {
  const files = fixture();
  files.set('scripts/docker/container-versions.conf', 'export ASPNET_TAG="10.0-noble"');
  assert.deepEqual(sdkPolicyErrors(files), []);
});

test('conditional setup and verification share the same language-matrix condition', () => {
  const files = fixture();
  files.set('.github/workflows/build.yml', goodWorkflow
    .replace('        with:', "        if: matrix.language == 'csharp'\n        with:")
    .replace('      - run:', "      - if: matrix.language == 'csharp'\n        run:"));
  assert.deepEqual(sdkPolicyErrors(files), []);
  files.set('.github/workflows/build.yml', files.get('.github/workflows/build.yml')
    .replace("      - if: matrix.language == 'csharp'", "      - if: matrix.language == 'javascript'"));
  assert.match(sdkPolicyErrors(files).join('\n'), /immediately verify/);
});

test('release setup and verification use the selected source policy, not the tooling policy', t => {
  const directory = tempRepo(t);
  const source = join(directory, 'source');
  mkdirSync(source);
  writeFileSync(join(source, 'global.json'), JSON.stringify({ sdk: { ...sdk, version: '10.0.403' } }));
  const release = yaml.load(readFileSync(join(root, '.github/workflows/consolidated-release.yml'), 'utf8'));
  const steps = release.jobs.build.steps;
  const index = steps.findIndex(step => step.uses?.startsWith('actions/setup-dotnet@'));
  const globalJson = steps[index].with['global-json-file'];
  assert.equal(globalJson, 'source/global.json');
  assert.equal(approvedSdkVersion(dirname(join(directory, globalJson))), '10.0.403');
  assert.equal(approvedSdkVersion(directory), '10.0.402');
  assert.equal(steps[index + 1].run, 'python3 scripts/dotnet-sdk.py verify --root source');
  assert.ok(steps.slice(0, index).some(step =>
    step.uses?.startsWith('actions/checkout@') && step.with.path === 'source' &&
      step.with.ref === '${{ needs.select.outputs.source_sha }}'));
  const files = fixture();
  files.set('.github/workflows/consolidated-release.yml', yaml.dump({ jobs: { build: { steps } } }));
  assert.deepEqual(sdkPolicyErrors(files), []);
  files.set('.github/workflows/consolidated-release.yml', files.get('.github/workflows/consolidated-release.yml')
    .replace('source/global.json', 'global.json'));
  assert.match(sdkPolicyErrors(files).join('\n'), /global-json-file: source\/global.json/);

  const result = spawnSync(python, ['-c', `
import importlib.util, json, sys
from types import SimpleNamespace
from unittest.mock import patch
from pathlib import Path
spec = importlib.util.spec_from_file_location("sdk", sys.argv[1])
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
source = Path(sys.argv[2])
def selected(command, **kwargs):
    assert kwargs["cwd"] == source
    return SimpleNamespace(stdout=json.loads((source / "global.json").read_text())["sdk"]["version"])
sys.argv = [sys.argv[1], "verify", "--root", str(source)]
with patch("subprocess.run", side_effect=selected):
    module.main()
`, join(directory, 'scripts/dotnet-sdk.py'), source], { encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /selected: 10\.0\.403; approved: 10\.0\.403/);
});

test('publisher loads from a clean checkout without root npm dependencies', t => {
  const directory = tempRepo(t);
  mkdirSync(join(directory, 'scripts/ci'));
  for (const file of readdirSync(join(root, 'scripts/ci')).filter(file => file.endsWith('.mjs'))) {
    copyFileSync(join(root, 'scripts/ci', file), join(directory, 'scripts/ci', file));
  }
  const result = spawnSync(process.execPath, ['--input-type=module', '-e',
    'await import("./scripts/ci/publish-release.mjs"); console.log("publisher loaded");'],
  { cwd: directory, encoding: 'utf8', env: { ...process.env, NODE_PATH: '' } });
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout.trim(), 'publisher loaded');
});

for (const [name, change, expected] of [
  ['floating SDK', files => files.set('global.json', JSON.stringify({ sdk: { ...sdk, version: '10.0.x' } })), /stable .NET 10/],
  ['old formatter feature band', files => files.set('global.json', JSON.stringify({ sdk: { ...sdk, version: '10.0.199' } })), />=10.0.200/],
  ['prerelease SDK', files => files.set('global.json', JSON.stringify({ sdk: { ...sdk, version: '10.0.403-preview.1' } })), /stable/],
  ['roll-forward', files => files.set('global.json', JSON.stringify({ sdk: { ...sdk, rollForward: 'latestPatch' } })), /rollForward:disable/],
  ['implicit prerelease selection', files => files.set('global.json', JSON.stringify({ sdk: { ...sdk, allowPrerelease: true } })), /allowPrerelease:false/],
  ['nested selector', files => files.set('src/global.json', JSON.stringify({ sdk })), /nested SDK selector/],
  ['floating CI SDK', files => files.set('.github/workflows/build.yml', goodWorkflow.replace('global-json-file: global.json', 'dotnet-version: 10.0.x')), /setup-dotnet must use only/],
  ['duplicate equal CI pin', files => files.set('.github/workflows/build.yml', goodWorkflow.replace('global-json-file: global.json', 'global-json-file: global.json\n          dotnet-version: 10.0.402')), /setup-dotnet must use only/],
  ['quoted inline YAML selector', files => files.set('.github/workflows/build.yml', goodWorkflow.replace('global-json-file: global.json', '"dotnet-version": "10.0.402"')), /setup-dotnet must use only/],
  ['wrong global file', files => files.set('.github/workflows/build.yml', goodWorkflow.replace('global-json-file: global.json', 'global-json-file: src/global.json')), /setup-dotnet must use only/],
  ['missing SDK log', files => files.set('.github/workflows/build.yml', goodWorkflow.replace('      - run: python3 scripts/dotnet-sdk.py verify\n', '')), /immediately verify/],
  ['optional verification', files => files.set('.github/workflows/build.yml', `${goodWorkflow}        continue-on-error: true\n`), /immediately verify/],
  ['literal container SDK', files => files.set('scripts/docker/dockerfiles/Dockerfile', goodDockerfile.replace('${SDK_TAG}', '10.0')), /independent SDK selector/],
  ['container fallback literal', files => files.set('scripts/docker/dockerfiles/Dockerfile', goodDockerfile.replace('${SDK_TAG}', '${SDK_TAG:-10.0}')), /independent SDK selector/],
  ['unrelated container selector variable', files => files.set('scripts/docker/dockerfiles/Dockerfile', goodDockerfile.replace('${SDK_TAG}', '${OTHER_TAG}')), /independent SDK selector/],
  ['Docker argument default', files => files.set('scripts/docker/dockerfiles/Dockerfile', goodDockerfile.replace('ARG SDK_TAG', 'ARG SDK_TAG=10.0.402-noble')), /without a default/],
  ['Docker omits global', files => files.set('scripts/docker/dockerfiles/Dockerfile', goodDockerfile.replace('COPY global.json ./\n', '')), /global.json and dotnet/],
  ['bootstrap literal', files => files.set('scripts/bootstrap.sh', 'REQ_DOTNET_VERSION=10.0.402'), /independent SDK selector/],
  ['JavaScript SDK literal', files => files.set('scripts/ci/build.mjs', "const sdkTag = '10.0.402-noble';"), /independent SDK selector/],
  ['MSBuild SDK override', files => files.set('src/Directory.Build.props', '<Project><PropertyGroup><NETCoreSdkVersion>10.0.402</NETCoreSdkVersion></PropertyGroup></Project>'), /MSBuild must not override/],
  ['bootstrap environment override', files => files.set('scripts/bootstrap.ps1', '$version = $env:DOTNET_VERSION'), /independent SDK selector/],
  ['installer floating channel', files => files.set('scripts/install.sh', 'bash dotnet-install.sh --channel 10.0'), /independent SDK selector/],
  ['continued Bash installer channel', files => files.set('scripts/install.sh', 'bash dotnet-install.sh \\\n  --channel 10.0'), /independent SDK selector/],
  ['continued PowerShell installer version', files => files.set('scripts/install.ps1', '& .\\dotnet-install.ps1 `\n  -Version 10.0.402'), /independent SDK selector/],
  ['devcontainer floating SDK feature', files => files.set('.devcontainer/devcontainer.json', '"ghcr.io/devcontainers/features/dotnet:2": {"version":"10.0"}'), /independent SDK selector/],
  ['missing SDK updater', files => files.set('.github/dependabot.yml', 'updates: []'), /root dotnet-sdk updates/],
  ['malformed workflow', files => files.set('.github/workflows/build.yml', 'jobs: [\n'), /invalid YAML/],
]) {
  test(`rejects ${name}`, () => {
    const files = fixture();
    change(files);
    assert.match(sdkPolicyErrors(files).join('\n'), expected);
  });
}

test('continued literal installers in the real Ubuntu bootstrap fail policy', () => {
  const original = readFileSync(join(root, 'scripts/bootstrap-ubuntu.sh'), 'utf8');
  assert.match(original, /ensure_dotnet_sdk/);
  for (const selector of ['--channel 10.0', '--version "10.0.402"']) {
    const files = fixture();
    files.set('scripts/bootstrap-ubuntu.sh', original.replace(
      'ensure_dotnet_sdk', `bash "$SCRIPT_DIR/dotnet-install.sh" \\\n  ${selector}`));
    assert.match(sdkPolicyErrors(files).join('\n'), /bootstrap-ubuntu.sh:\d+: independent SDK selector/);
  }
});

test('reader resolves global.json independently of cwd and derives the Docker tag', t => {
  const directory = tempRepo(t);
  for (const [command, expected] of [['version', sdk.version], ['tag', `${sdk.version}-noble`]]) {
    const result = spawnSync(python, [join(directory, 'scripts/dotnet-sdk.py'), command], { cwd: tmpdir(), encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stdout.trim(), expected);
  }
  assert.equal(approvedSdkVersion(directory), sdk.version);
  writeFileSync(join(directory, 'global.json'), JSON.stringify({ sdk: { ...sdk, rollForward: 'latestMinor' } }));
  const rejected = spawnSync(python, [join(directory, 'scripts/dotnet-sdk.py'), 'version'], { encoding: 'utf8' });
  assert.equal(rejected.status, 1);
  assert.match(rejected.stderr, /rollForward:disable/);
});

test('selected SDK mismatch fails rather than accepting another installed SDK', t => {
  const directory = tempRepo(t);
  const helper = join(directory, 'scripts/dotnet-sdk.py');
  const result = spawnSync(python, ['-c', `
import importlib.util, sys
from types import SimpleNamespace
from unittest.mock import patch
spec = importlib.util.spec_from_file_location("sdk", sys.argv[1])
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
sys.argv = [sys.argv[1], "verify"]
with patch("subprocess.run", return_value=SimpleNamespace(stdout="10.0.999\\n")):
    try:
        module.main()
    except ValueError as error:
        print(error)
        sys.exit(7)
`, helper], { encoding: 'utf8' });
  assert.equal(result.status, 7, result.stderr);
  assert.match(result.stdout, /differs from global.json/);
});

function shellFixture(t) {
  const directory = tempRepo(t);
  mkdirSync(join(directory, 'bin'));
  mkdirSync(join(directory, 'scripts/docker'));
  const pythonPath = spawnSync(python, ['-c', 'import sys; print(sys.executable)'], { encoding: 'utf8' }).stdout.trim();
  writeFileSync(join(directory, 'bin/python3'), `#!/usr/bin/env bash\nexec "${pythonPath.replaceAll('\\', '/')}" "$@"\n`, { mode: 0o755 });
  for (const file of ['dotnet-sdk.sh', 'common-utils.sh', 'docker/container-versions.conf']) {
    copyFileSync(join(root, 'scripts', file), join(directory, 'scripts', file));
  }
  return {
    directory,
    env: { ...process.env, PATH: `${join(directory, 'bin')}${process.platform === 'win32' ? ';' : ':'}${process.env.PATH}`,
      SUDO_USER: '', DOTNET_VERSION: '99.0.999', SDK_TAG: '99.0-wrong' },
    shellRoot: directory.replaceAll('\\', '/'),
  };
}

test('container tooling ignores a stale environment SDK tag and follows a changed global pin', t => {
  const { directory, env, shellRoot } = shellFixture(t);
  for (const version of [sdk.version, '10.0.403']) {
    writeFileSync(join(directory, 'global.json'), JSON.stringify({ sdk: { ...sdk, version } }));
    const result = spawnSync('bash', ['-c', `set -euo pipefail; source "${shellRoot}/scripts/docker/container-versions.conf"; printf '%s' "$SDK_TAG"`],
      { env, encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stdout, `${version}-noble`);
  }
});

test('bootstrap attempts the exact SDK when another SDK exists and propagates installer failure', t => {
  const { directory, env, shellRoot } = shellFixture(t);
  writeFileSync(join(directory, 'bin/dotnet'), '#!/usr/bin/env bash\nprintf "9.0.100\\n"\n', { mode: 0o755 });
  writeFileSync(join(directory, 'scripts/dotnet-install.sh'), '#!/usr/bin/env bash\nprintf "%s\\n" "$@" > "$TEST_INSTALL_LOG"\nexit 42\n');
  env.TEST_INSTALL_LOG = `${shellRoot}/install.log`;
  const result = spawnSync('bash', ['-c', `set -euo pipefail; source "${shellRoot}/scripts/dotnet-sdk.sh"; ensure_dotnet_sdk`],
    { env, encoding: 'utf8' });
  assert.equal(result.status, 1, result.stderr);
  const args = readFileSync(join(directory, 'install.log'), 'utf8').split(/\r?\n/);
  assert.equal(args[args.indexOf('--version') + 1], sdk.version);
  assert.ok(!args.includes('--channel'));
});

test('cached devcontainer repair selects the approved SDK in a fresh terminal/editor process', t => {
  const { directory, env, shellRoot } = shellFixture(t);
  const config = readFileSync(join(root, '.devcontainer/devcontainer.json'), 'utf8');
  const remote = JSON.parse(config.match(/"remoteEnv":\s*(\{[\s\S]*?\n  \})/)[1]);
  assert.equal(remote.DOTNET_ROOT, '/home/node/.dotnet');
  assert.equal(remote.PATH, '/home/node/.dotnet:${containerEnv:PATH}');
  assert.match(readFileSync(join(root, '.devcontainer/post-create.sh'), 'utf8'),
    /ensure_dotnet_sdk "\$HOME\/\.dotnet"/);
  mkdirSync(join(directory, 'home'));
  writeFileSync(join(directory, 'bin/dotnet'), '#!/usr/bin/env bash\nprintf "9.0.100\\n"\n', { mode: 0o755 });
  writeFileSync(join(directory, 'scripts/dotnet-install.sh'), `#!/usr/bin/env bash
set -eu
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) version="$2"; shift 2 ;;
    --install-dir) destination="$2"; shift 2 ;;
    *) exit 2 ;;
  esac
done
mkdir -p "$destination"
printf '#!/usr/bin/env bash\\nprintf "%%s\\\\n" "%s"\\n' "$version" > "$destination/dotnet"
chmod +x "$destination/dotnet"
`);
  const pythonPath = spawnSync(python, ['-c', 'import sys; print(sys.executable)'], { encoding: 'utf8' })
    .stdout.trim().replaceAll('\\', '/');
  // Model dotnet discovery through Bash so executable fixture shims work on Windows too.
  writeFileSync(join(directory, 'bin/python3'), `#!/usr/bin/env bash
if [[ "\${2:-}" == verify ]]; then
  expected="$("${pythonPath}" "$1" version)"
  [[ "$(dotnet --version)" == "$expected" ]]
else
  exec "${pythonPath}" "$@"
fi
`, { mode: 0o755 });
  const home = `export HOME="$(cd "${shellRoot}/home" && pwd)"`;
  const provision = spawnSync('bash', ['-c',
    `set -euo pipefail; ${home}; source "${shellRoot}/scripts/dotnet-sdk.sh"; ensure_dotnet_sdk "$HOME/.dotnet"`],
  { env, encoding: 'utf8' });
  assert.equal(provision.status, 0, provision.stderr);
  const remoteRoot = remote.DOTNET_ROOT.replace('/home/node', '$HOME');
  const remotePath = remote.PATH.replace('/home/node', '$HOME').replace('${containerEnv:PATH}', '$PATH');
  const fresh = spawnSync('bash', ['-c',
    `set -euo pipefail; ${home}; export DOTNET_ROOT="${remoteRoot}"; export PATH="${remotePath}"; dotnet --version; command -v dotnet`],
  { env, encoding: 'utf8' });
  assert.equal(fresh.status, 0, fresh.stderr);
  const [version, selectedPath] = fresh.stdout.trim().split(/\r?\n/);
  assert.equal(version, sdk.version);
  assert.match(selectedPath, /\/home\/\.dotnet\/dotnet$/);
});

test('local worker build arrays pass the approved SDK tag to both actual Docker invocations', t => {
  const { directory, env, shellRoot } = shellFixture(t);
  const content = readFileSync(join(root, 'scripts/start-all-local-with-workers.sh'), 'utf8');
  const start = content.indexOf('  ORCA_BIN_CMD=(docker build)');
  const endMarker = '  "${ORCA_WORKER_CMD[@]}"';
  const end = content.indexOf(endMarker, start);
  assert.ok(start > 0 && end > start);
  const capture = join(directory, 'docker-args');
  env.TEST_CAPTURE = capture;
  const result = spawnSync('bash', ['-c', `set -euo pipefail
source "${shellRoot}/scripts/docker/container-versions.conf"
ROOT_DIR="${shellRoot}"
ORCA_VERSION="$ORCASLICER_VERSION"
ORCA_SHA256="$ORCASLICER_SHA256"
DOCKER_BUILD_PLATFORM=linux/arm64
docker() { printf '%s\\0' "$@" >> "$TEST_CAPTURE"; printf '\\0' >> "$TEST_CAPTURE"; }
info() { :; }
validate_orcaslicer_binary_image() { :; }
${content.slice(start, end + endMarker.length)}
`], { env, encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  const calls = readFileSync(capture, 'utf8').split('\0\0').filter(Boolean).map(call => call.split('\0'));
  assert.equal(calls.length, 2);
  for (const [index, args] of calls.entries()) {
    assert.equal(args[0], 'build');
    assert.equal(args[args.indexOf('--target') + 1], ['orcaslicer-binaries', 'orcaslicer-worker'][index]);
    assert.ok(args.some((arg, i) => args[i - 1] === '--build-arg' && arg === `SDK_TAG=${sdk.version}-noble`));
  }
});

test('optimization build entry point passes the derived SDK to every Docker build', t => {
  const { directory, env, shellRoot } = shellFixture(t);
  copyFileSync(join(root, 'scripts/test-orcaslicer-optimization.sh'),
    join(directory, 'scripts/test-orcaslicer-optimization.sh'));
  writeFileSync(join(directory, 'Dockerfile.multistage'), '');
  mkdirSync(join(directory, 'src/orcaslicer-worker'), { recursive: true });
  writeFileSync(join(directory, 'src/orcaslicer-worker/Program.cs'), '');
  const capture = join(directory, 'docker-args');
  env.TEST_CAPTURE = capture;
  const result = spawnSync('bash', ['-c', `set -euo pipefail
docker() {
  if [[ "$1" == build ]]; then printf '%s\\0' "$@" >> "$TEST_CAPTURE"; printf '\\0' >> "$TEST_CAPTURE"; fi
  if [[ "$1" == images ]]; then printf 'orcaslicer-binaries printfarmer-orcaslicer-worker\\n'; fi
}
cd "${shellRoot}"
source scripts/test-orcaslicer-optimization.sh
`], { env, encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  const builds = readFileSync(capture, 'utf8').split('\0\0').filter(Boolean).map(call => call.split('\0'));
  assert.equal(builds.length, 3);
  for (const args of builds) {
    assert.ok(args.some((arg, i) => args[i - 1] === '--build-arg' && arg === `SDK_TAG=${sdk.version}-noble`));
  }
});

test('PowerShell Compose renders the approved SDK under unset and stale environment selectors', t => {
  const directory = tempRepo(t);
  for (const stale of [undefined, '99.0-wrong']) {
    const env = { ...process.env };
    delete env.SDK_TAG;
    if (stale) env.SDK_TAG = stale;
    const result = spawnSync('pwsh', ['-NoProfile', '-File', join(root, 'scripts/compose-generator.ps1'),
      '-Architecture', 'monolithic', '-OutputDir', directory], { env, encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr || result.error?.message);
    const rendered = yaml.load(readFileSync(join(directory, 'docker-compose.yml'), 'utf8'));
    const builds = Object.values(rendered.services).filter(service => service.build);
    assert.ok(builds.length > 0);
    for (const service of builds) assert.equal(service.build.args.SDK_TAG, `${approvedSdkVersion(root)}-noble`);
  }
});

test('repository workflows, bootstraps and Docker templates satisfy SDK policy', () => {
  assert.deepEqual(sdkPolicyErrors(repositorySdkFiles(root)), []);
  const ci = readFileSync(join(root, '.github/workflows/ci.yml'), 'utf8');
  assert.match(ci, /run: node scripts\/ci\/check-dotnet-sdk\.mjs/);
  assert.match(ci, /run: node --test scripts\/ci\/tests\/test-dotnet-sdk\.mjs/);
});
