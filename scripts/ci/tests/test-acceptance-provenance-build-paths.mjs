import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';

const pfdev = readFileSync('scripts/pfdev', 'utf8');
const deploy = readFileSync('scripts/deploy-docker.sh', 'utf8');
const registryBuild = readFileSync('scripts/build-and-push-registry.sh', 'utf8');
const legacyDockerfile = readFileSync('scripts/docker/dockerfiles/Dockerfile', 'utf8');
const frontendDockerfile = readFileSync(
  'scripts/docker/dockerfiles/Dockerfile.frontend',
  'utf8',
);
const dailyValidationSmoke = readFileSync(
  'scripts/ci/smoke-daily-validation-stack.sh',
  'utf8',
);
const splitTopologySmoke = readFileSync('tests/test-split-topology-route-smoke.sh', 'utf8');
const publishWorkflow = readFileSync('.github/workflows/docker-publish.yml', 'utf8');
const buildMetadataPath = path.resolve('scripts/build-metadata.sh');
const repositoryRoot = process.cwd();

function resolveLocalBuildGitSha(requestedSha = '') {
  return execFileSync(
    'bash',
    [
      '-c',
      'source "$1" >/dev/null; resolve_local_build_git_sha "$2" "$3"',
      'resolve-local-build-git-sha',
      buildMetadataPath,
      repositoryRoot,
      requestedSha,
    ],
    { encoding: 'utf8' },
  ).trim();
}

test('local build SHA resolution binds supplied metadata to repository HEAD', () => {
  const head = execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim();
  assert.equal(resolveLocalBuildGitSha(), head);
  assert.equal(resolveLocalBuildGitSha(head.toUpperCase()), head);
  assert.equal(
    execFileSync(
      'bash',
      [
        '-c',
        'SCRIPT_DIR=caller-directory; source "$1"; printf %s "$SCRIPT_DIR"',
        'source-build-metadata',
        buildMetadataPath,
      ],
      { encoding: 'utf8' },
    ),
    'caller-directory',
  );

  const mismatch = spawnSync(
    'bash',
    [
      '-c',
      'source "$1" >/dev/null; resolve_local_build_git_sha "$2" "$3"',
      'resolve-local-build-git-sha',
      buildMetadataPath,
      repositoryRoot,
      'a'.repeat(40),
    ],
    { encoding: 'utf8' },
  );
  assert.notEqual(mismatch.status, 0);
  assert.match(mismatch.stderr, /must match the checked-out source commit/);
});

test('pfdev injects a validated full commit before compose builds', () => {
  assert.match(pfdev, /ensure_build_git_sha\(\)/);
  assert.match(pfdev, /resolve_local_build_git_sha "\$REPO_ROOT"/);
  assert.match(pfdev, /ensure_build_git_sha \|\| return 1[\s\S]*docker compose build --no-cache/);
});

test('deployment build scripts validate commit identity only on local build paths', () => {
  for (const script of [deploy, registryBuild]) {
    assert.match(script, /resolve_local_build_git_sha/);
  }
  assert.match(
    deploy,
    /elif \[ "\$DRY_RUN" = "true" \][\s\S]*else[\s\S]*resolve_local_build_git_sha[\s\S]*compose_build_args/,
  );
  assert.ok(deploy.indexOf('resolve_local_build_git_sha') > deploy.indexOf('elif [ "$DRY_RUN" = "true" ]'));
});

test('active Dockerfile variants propagate full commit metadata into production builds', () => {
  assert.match(frontendDockerfile, /ARG GIT_SHA=unknown[\s\S]*VITE_GIT_SHA=\$\{GIT_SHA\}/);
  assert.match(legacyDockerfile, /ARG GIT_SHA=unknown/);
  assert.match(legacyDockerfile, /ENV VITE_GIT_SHA=\$\{GIT_SHA\}/);
  assert.match(legacyDockerfile, /-p:GitCommitSha=\$\{GIT_SHA\}/);
});

test('release workflow injects the full source commit into container builds', () => {
  assert.match(publishWorkflow, /echo "full=\$\(git rev-parse HEAD\)"/);
  assert.match(publishWorkflow, /GIT_SHA=\$\{\{ steps\.gitsha\.outputs\.full \}\}/);
  assert.match(publishWorkflow, /VITE_GIT_SHA=\$\{\{ steps\.gitsha\.outputs\.full \}\}/);
  assert.doesNotMatch(publishWorkflow, /git rev-parse --short HEAD/);
});

test('live split-topology builds inject the exact commit under test', () => {
  assert.match(splitTopologySmoke, /resolve_local_build_git_sha "\$REPO_ROOT"/);
  assert.match(splitTopologySmoke, /export GIT_SHA[\s\S]*compose up -d --build/);
});

test('local daily validation builds bind expected provenance to checkout HEAD', () => {
  assert.match(
    dailyValidationSmoke,
    /if \[\[ "\$USE_REGISTRY" != "true" \]\]; then[\s\S]*resolve_local_build_git_sha "\$REPO_ROOT" "\$EXPECTED_ACCEPTANCE_SHA"/,
  );
  assert.match(dailyValidationSmoke, /export GIT_SHA="\$EXPECTED_ACCEPTANCE_SHA"/);
});
