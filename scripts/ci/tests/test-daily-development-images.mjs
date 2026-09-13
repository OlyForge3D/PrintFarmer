import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const workflow = readFileSync('.github/workflows/daily-development-images.yml', 'utf8');
const dockerfile = readFileSync('scripts/docker/dockerfiles/Dockerfile.multistage', 'utf8');
const registry = readFileSync('scripts/docker/compose-templates/docker-compose.daily-registry.yml', 'utf8');
const validation = readFileSync('scripts/docker/compose-templates/docker-compose.daily-validation.yml', 'utf8');
const emulator = readFileSync('scripts/docker/compose-templates/docker-compose.moonraker-emulator.yml', 'utf8');
const services = ['api', 'frontend', 'slicer-host', 'printer-discovery', 'orcaslicer-worker', 'moonraker-emulator'];

test('daily validation selects and pins development HEAD without publication credentials', () => {
  assert.match(workflow, /cron: '17 9 \* \* \*'/);
  assert.match(workflow, /ref: refs\/heads\/development/);
  assert.match(workflow, /test "\$commit_sha" = "\$remote_sha"/);
  assert.match(workflow, /ref: \$\{\{ needs\.source\.outputs\.commit_sha \}\}/);
  assert.doesNotMatch(workflow, /packages: write|id-token: write|docker push|docker tag|push: true/);
  assert.match(workflow, /push: false/);
  assert.match(workflow, /consolidated-release.yml/);
});

test('all six validation builds retain live runtime health probes', () => {
  for (const service of services) assert.ok(workflow.includes(`- image: ${service}`));
  assert.equal(workflow.match(/uses: docker\/build-push-action@v7/g)?.length, 1);
  assert.match(workflow, /cache-from: type=gha,scope=daily-/);
  assert.match(workflow, /cache-to: type=gha,mode=max,scope=daily-/);
  for (const url of ['localhost:5000/healthz', 'localhost:80/health', 'localhost:5246/healthz',
    'localhost:5247/api/discovery/health', 'localhost:8080/healthz', 'localhost:7125/healthz']) {
    assert.ok(workflow.includes(url));
  }
  assert.match(workflow, /docker exec "\$container" curl --fail/);
  assert.match(workflow, /WorkerAuth__SharedKey=daily-image-smoke-only-worker-key/);
  assert.match(workflow, /ASPNETCORE_URLS=http:\/\/\+:5247/);
  assert.doesNotMatch(workflow, /--entrypoint dotnet "\$IMAGE" --info/);
});

test('canonical API and slicer builds retain runtime-selected SQLite migrations', () => {
  assert.match(dockerfile, /dotnet build \.\/migrations\/Farm\.Slicer\.Migrations\.Sqlite\//);
  assert.match(dockerfile, /\[ -f \/app\/publish\/api\/plugins\/slicer\/Farm\.Slicer\.Migrations\.Sqlite\.dll \]/);
});

test('historical registry overlays remain digest-only and contain all six application images', () => {
  for (const name of ['API', 'FRONTEND', 'SLICER_HOST', 'PRINTER_DISCOVERY', 'ORCASLICER_WORKER', 'MOONRAKER_EMULATOR']) {
    assert.ok(registry.includes(`PRINTFARMER_${name}_IMAGE`));
  }
  assert.match(validation, /MoonrakerEmulatorSeed__Enabled: "true"/);
  assert.match(validation, /Discovery__DeterministicFixtures__Enabled: "true"/);
  assert.match(validation, /Worker__MaxConcurrentJobs: "1"/);
  assert.match(validation, /EnablePeriodicDiscovery: "false"/);
  assert.doesNotMatch(validation, /TestEmulator__Enabled|TestEmulator__MockDiscovery|TestEmulator__MockSpoolman/);
});

test('one emulator image retains four hardened instances and no live offline listener', () => {
  for (const scenario of ['ready', 'printing', 'paused', 'shutdown']) {
    assert.match(emulator, new RegExp(`^  moonraker-${scenario}:`, 'm'));
    assert.match(validation, new RegExp(`^  moonraker-${scenario}:`, 'm'));
    assert.match(registry, new RegExp(`^  moonraker-${scenario}:`, 'm'));
  }
  assert.equal((emulator.match(/^\s{2}moonraker-[a-z-]+:/gm) ?? []).length, 4);
  assert.equal((registry.match(/image: \$\{PRINTFARMER_MOONRAKER_EMULATOR_IMAGE:\?/g) ?? []).length, 4);
  assert.match(emulator, /read_only: true/);
  assert.match(emulator, /cap_drop:\s*\n\s*- ALL/);
  assert.match(emulator, /target: moonraker-emulator-runtime/);
  assert.doesNotMatch(emulator, /^\s+(?:ports|cap_add):/m);
  assert.ok(!emulator.includes('/var/run/docker.sock'));
  assert.doesNotMatch(validation, /^\s{2}moonraker-offline:/m);
  assert.match(emulator, /MOONRAKER_EMULATOR_ENABLE_CONTROL_API:-false/);
});
