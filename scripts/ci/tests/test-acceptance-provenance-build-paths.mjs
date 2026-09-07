import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const pfdev = readFileSync('scripts/pfdev', 'utf8');
const deploy = readFileSync('scripts/deploy-docker.sh', 'utf8');
const registryBuild = readFileSync('scripts/build-and-push-registry.sh', 'utf8');
const legacyDockerfile = readFileSync('scripts/docker/dockerfiles/Dockerfile', 'utf8');
const frontendDockerfile = readFileSync(
  'scripts/docker/dockerfiles/Dockerfile.frontend',
  'utf8',
);
const publishWorkflow = readFileSync('.github/workflows/docker-publish.yml', 'utf8');

test('pfdev injects a validated full commit before compose builds', () => {
  assert.match(pfdev, /ensure_build_git_sha\(\)/);
  assert.match(pfdev, /git -C "\$REPO_ROOT" rev-parse HEAD/);
  assert.match(pfdev, /\^\[0-9a-fA-F\]\{40\}\$/);
  assert.match(pfdev, /ensure_build_git_sha[\s\S]*docker compose build --no-cache/);
});

test('deployment build scripts reject missing or non-full commit identities early', () => {
  for (const script of [deploy, registryBuild]) {
    assert.match(script, /\^\[0-9a-fA-F\]\{40\}\$/);
    assert.match(script, /full 40-character GIT_SHA is required/);
  }
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
