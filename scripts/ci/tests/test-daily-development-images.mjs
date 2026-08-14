import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const workflow = readFileSync('.github/workflows/daily-development-images.yml', 'utf8');
const dockerfile = readFileSync(
  'scripts/docker/dockerfiles/Dockerfile.multistage',
  'utf8',
);
const dailyRegistryOverlay = readFileSync(
  'scripts/docker/compose-templates/docker-compose.daily-registry.yml',
  'utf8',
);
const validationOverlay = readFileSync(
  'scripts/docker/compose-templates/docker-compose.daily-validation.yml',
  'utf8',
);
const documentation = readFileSync('docs/DAILY_DEVELOPMENT_IMAGES.md', 'utf8');
const moonrakerEmulatorTemplate = readFileSync(
  'scripts/docker/compose-templates/docker-compose.moonraker-emulator.yml',
  'utf8',
);
const triggers = workflow.slice(workflow.indexOf('on:'), workflow.indexOf('concurrency:'));

const services = [
  ['api', 'api-runtime'],
  ['frontend', 'frontend-runtime'],
  ['slicer-host', 'slicer-host-runtime'],
  ['printer-discovery', 'printer-discovery-runtime'],
  ['orcaslicer-worker', 'orcaslicer-worker'],
  ['moonraker-emulator', 'moonraker-emulator-runtime'],
];

// One digest-pinned moonraker-emulator IMAGE is instantiated as four
// separate compose SERVICES (not aliases of one container). The
// "exactly one" requirement in this repository applies to the OrcaSlicer
// worker, not to emulator replicas.
const moonrakerEmulatorInstances = [
  'moonraker-ready',
  'moonraker-printing',
  'moonraker-paused',
  'moonraker-shutdown',
];

test('daily workflow binds every build to development HEAD', () => {
  assert.match(workflow, /cron: '17 9 \* \* \*'/);
  assert.match(workflow, /workflow_dispatch:/);
  assert.match(workflow, /ref: refs\/heads\/development/);
  assert.match(workflow, /commit_sha="\$\(git rev-parse HEAD\)"/);
  assert.match(
    workflow,
    /remote_sha="\$\(git ls-remote origin refs\/heads\/development \| awk '\{print \$1\}'\)"/,
  );
  assert.match(workflow, /ref: \$\{\{ needs\.source\.outputs\.commit_sha \}\}/);
  assert.doesNotMatch(triggers, /^\s+push:/m);
});

test('all required images validate before publication', () => {
  for (const [image, target] of services) {
    assert.match(workflow, new RegExp(`- image: ${image}\\s+target: ${target}`));
  }

  assert.match(workflow, /needs: \[source, validate-images\]/);
  assert.match(workflow, /file: \$\{\{ env\.DOCKERFILE \}\}/);
  assert.match(workflow, /cache-from: type=gha,scope=daily-/);
  assert.match(workflow, /cache-to: type=gha,mode=max,scope=daily-/);
  assert.match(workflow, /Smoke check running image/);
  assert.match(workflow, /docker run --detach --name "\$container"/);
  for (const healthPath of [
    'localhost:5000/healthz',
    'localhost:80/health',
    'localhost:5246/healthz',
    'localhost:5246/api/discovery/health',
    'localhost:8080/healthz',
    'localhost:7125/healthz',
  ]) {
    assert.match(workflow, new RegExp(healthPath.replace('/', '\\/')));
  }
  assert.doesNotMatch(workflow, /--entrypoint dotnet "\$IMAGE" --info/);
  assert.match(workflow, /Upload validated image archive/);
  assert.match(workflow, /Download validated image archives/);
  assert.match(workflow, /docker load --input "\$archive\/image\.tar"/);
  assert.equal(workflow.match(/uses: docker\/build-push-action@v7/g)?.length, 1);
  assert.match(workflow, /packages: write/);
  assert.match(workflow, /password: \$\{\{ secrets\.GITHUB_TOKEN \}\}/);
});

test('API image includes the runtime-selected SQLite slicer migrations', () => {
  assert.match(
    dockerfile,
    /dotnet build \.\/migrations\/Farm\.Slicer\.Migrations\.Sqlite\/Farm\.Slicer\.Migrations\.Sqlite\.csproj -c Release -o \/app\/publish\/api\/plugins\/slicer/,
  );
  assert.match(
    dockerfile,
    /\[ -f \/app\/publish\/api\/plugins\/slicer\/Farm\.Slicer\.Migrations\.Sqlite\.dll \]/,
  );
});

test('API and slicer-host smoke checks supply their required worker shared key', () => {
  const apiSmoke = workflow.slice(
    workflow.indexOf('            api)'),
    workflow.indexOf('            slicer-host)'),
  );
  const slicerHostSmoke = workflow.slice(
    workflow.indexOf('            slicer-host)'),
    workflow.indexOf('            orcaslicer-worker)'),
  );

  assert.match(
    apiSmoke,
    /--env 'WorkerAuth__SharedKey=daily-image-smoke-only-worker-key'/,
  );
  assert.match(
    slicerHostSmoke,
    /--env 'WorkerAuth__SharedKey=daily-image-smoke-only-worker-key'/,
  );
});

test('publication uses run-unique tags and emits one coherent digest manifest', () => {
  assert.match(
    workflow,
    /TAG: sha-\$\{\{ needs\.source\.outputs\.commit_sha \}\}-run-\$\{\{ github\.run_id \}\}-attempt-\$\{\{ github\.run_attempt \}\}/,
  );
  assert.doesNotMatch(workflow, /needs\.source\.outputs\.image_tag/);
  assert.match(workflow, /artifact_attempt: \$\{\{ steps\.source\.outputs\.artifact_attempt \}\}/);
  assert.match(
    workflow,
    /daily-validated-\$\{\{ matrix\.image \}\}-attempt-\$\{\{ needs\.source\.outputs\.artifact_attempt \}\}/,
  );
  assert.match(workflow, /name: Publish validated image set/);
  assert.match(workflow, /services=\(api frontend slicer-host printer-discovery orcaslicer-worker moonraker-emulator\)/);
  assert.match(workflow, /publication_attempt: \$\{\{ steps\.publish\.outputs\.publication_attempt \}\}/);
  assert.match(
    workflow,
    /daily-digest-records-attempt-\$\{\{ needs\.publish-images\.outputs\.publication_attempt \}\}/,
  );
  assert.match(
    workflow,
    /IMAGE_TAG: sha-\$\{\{ needs\.source\.outputs\.commit_sha \}\}-run-\$\{\{ github\.run_id \}\}-attempt-\$\{\{ needs\.publish-images\.outputs\.publication_attempt \}\}/,
  );
  assert.doesNotMatch(workflow, /value=latest|:latest/);
  assert.doesNotMatch(workflow, /docker manifest inspect "\$reference"/);
  assert.match(workflow, /docker tag "\$validated_image" "\$reference"/);
  assert.match(workflow, /docker push "\$reference"/);
  assert.match(workflow, /Final published \$service tag does not match the validated image/);
  assert.match(workflow, /Expected six image digest records/);
  assert.match(workflow, /name: daily-development-image-set/);
  assert.match(workflow, /retention-days: 90/);
  assert.match(workflow, /overwrite: true/);
  assert.match(workflow, /reference: \(\.image \+ "@" \+ \.digest\)/);
  assert.match(workflow, /test\("\^sha256:\[0-9a-f\]\{64\}\$"\)/);
});

test('registry and local validation overlays cover the complete stack', () => {
  for (const [service] of services) {
    // The registry overlay pins compose SERVICE names to published IMAGES.
    // moonraker-emulator is published as one image but instantiated as four
    // named services (see moonrakerEmulatorInstances below), so it has no
    // same-named compose service of its own in this overlay.
    if (service === 'moonraker-emulator') {
      continue;
    }
    assert.match(dailyRegistryOverlay, new RegExp(`^  ${service}:`, 'm'));
  }
  for (const instance of moonrakerEmulatorInstances) {
    assert.match(dailyRegistryOverlay, new RegExp(`^  ${instance}:`, 'm'));
  }

  for (const variable of [
    'PRINTFARMER_API_IMAGE',
    'PRINTFARMER_FRONTEND_IMAGE',
    'PRINTFARMER_SLICER_HOST_IMAGE',
    'PRINTFARMER_PRINTER_DISCOVERY_IMAGE',
    'PRINTFARMER_ORCASLICER_WORKER_IMAGE',
    'PRINTFARMER_MOONRAKER_EMULATOR_IMAGE',
  ]) {
    assert.match(dailyRegistryOverlay, new RegExp(variable));
  }
  // All four instances must pin to the same published image reference.
  assert.equal(
    (dailyRegistryOverlay.match(/image: \$\{PRINTFARMER_MOONRAKER_EMULATOR_IMAGE:\?/g) ?? [])
      .length,
    moonrakerEmulatorInstances.length,
  );

  assert.match(validationOverlay, /ASPNETCORE_ENVIRONMENT: Development/);
  assert.match(validationOverlay, /MoonrakerEmulatorSeed__Enabled: "true"/);
  assert.match(validationOverlay, /Discovery__DeterministicFixtures__Enabled: "true"/);
  assert.doesNotMatch(validationOverlay, /TestEmulator__Enabled/);
  assert.doesNotMatch(validationOverlay, /TestEmulator__MockDiscovery/);
  assert.doesNotMatch(validationOverlay, /TestEmulator__MockSpoolman/);
  assert.match(validationOverlay, /EnablePeriodicDiscovery: "false"/);
  assert.match(validationOverlay, /Worker__MaxConcurrentJobs: "1"/);
  // No compose service is literally named "moonraker-emulator" or
  // "moonraker-offline" in the validation overlay: offline is a seed-only
  // hostname with no listener, and each real instance has its own name.
  assert.doesNotMatch(validationOverlay, /^\s{2}moonraker-emulator:/m);
  assert.doesNotMatch(validationOverlay, /^\s{2}moonraker-offline:/m);
  for (const instance of moonrakerEmulatorInstances) {
    assert.match(validationOverlay, new RegExp(`^  ${instance}:`, 'm'));
  }
  assert.equal(
    (validationOverlay.match(/Emulator__EnableControlApi: "true"/g) ?? []).length,
    moonrakerEmulatorInstances.length,
  );
  for (const binding of [
    '127.0.0.1:${POSTGRES_PORT:-15432}:5432',
    '127.0.0.1:${API_PORT:-15245}:5245',
    '127.0.0.1:${SLICER_HOST_PORT:-15246}:5246',
    '127.0.0.1:${HTTP_PORT:-18080}:80',
    '127.0.0.1:${HTTPS_PORT:-18443}:443',
    '127.0.0.1:${MOONRAKER_EMULATOR_PORT:-17125}:7125',
    '127.0.0.1:${MOONRAKER_EMULATOR_PRINTING_PORT:-17126}:7125',
    '127.0.0.1:${MOONRAKER_EMULATOR_PAUSED_PORT:-17127}:7125',
    '127.0.0.1:${MOONRAKER_EMULATOR_SHUTDOWN_PORT:-17128}:7125',
  ]) {
    assert.ok(validationOverlay.includes(binding), `Missing port binding: ${binding}`);
  }
  assert.match(validationOverlay, /name: printfarmer-daily-validation/);
  assert.match(validationOverlay, /container_name: !reset null/);
  assert.match(validationOverlay, /name: printfarmer-daily-validation-network/);
  assert.match(
    validationOverlay,
    /printer-discovery:\s+container_name: !reset null\s+volumes: !override \[\]/,
  );
  assert.match(
    documentation,
    /\.\/scripts\/generate-certs\.sh "\$STACK_DIR\/deploy\/nginx\/certs"/,
  );
  assert.match(documentation, /--project-name "\$COMPOSE_PROJECT_NAME"/);
  assert.match(documentation, /export API_PORT=15245/);
  assert.match(documentation, /export POSTGRES_USER=printfarmer/);
  assert.match(documentation, /unset POSTGRES_USER/);
});

test('moonraker-emulator instances are internal-only and hardened by default, with no live offline listener', () => {
  // Exactly the four intentional replicas — the repository's "exactly one"
  // requirement is scoped to the OrcaSlicer worker, not emulator instances.
  for (const instance of moonrakerEmulatorInstances) {
    assert.match(moonrakerEmulatorTemplate, new RegExp(`^  ${instance}:`, 'm'));
  }
  assert.equal(
    (moonrakerEmulatorTemplate.match(/^\s{2}moonraker-[a-z-]+:/gm) ?? []).length,
    moonrakerEmulatorInstances.length,
  );
  // "offline" and any Host-alias-based seeding shortcut must not exist here:
  // offline is seeded with no listener at all, on purpose. moonraker-ready is
  // the only instance with network aliases, and only the two discovery-only
  // hostnames — never a seed-scenario alias for another instance.
  assert.doesNotMatch(moonrakerEmulatorTemplate, /^\s{2}moonraker-emulator:/m);
  assert.doesNotMatch(moonrakerEmulatorTemplate, /^\s{2}moonraker-offline:/m);
  assert.equal(
    (moonrakerEmulatorTemplate.match(/^\s*aliases:\s*$/gm) ?? []).length,
    1,
    'only moonraker-ready should declare discovery-only network aliases',
  );
  assert.match(
    moonrakerEmulatorTemplate,
    /moonraker-ready:[\s\S]*?aliases:\s*\n(?:\s*#[^\n]*\n)*\s*- moonraker-discovery-voron\s*\n\s*- moonraker-discovery-prusa/,
  );
  for (const instance of moonrakerEmulatorInstances) {
    if (instance === 'moonraker-ready') {
      continue;
    }
    assert.match(moonrakerEmulatorTemplate, new RegExp(`${instance}:[\\s\\S]*?networks:\\s*\\n\\s*- printfarmer-network`));
  }

  assert.match(moonrakerEmulatorTemplate, /target: moonraker-emulator-runtime/);
  // Each instance reuses the same build anchor (a single image, not four
  // separately-defined build configs).
  assert.equal(
    (moonrakerEmulatorTemplate.match(/<<: \*moonraker-emulator-build/g) ?? []).length,
    moonrakerEmulatorInstances.length,
  );
  // Every instance shares the same digest-pinned image (one image, four
  // isolated runtime instances).
  assert.equal(
    (moonrakerEmulatorTemplate.match(/image: printfarmer-moonraker-emulator/g) ?? []).length,
    moonrakerEmulatorInstances.length,
  );

  const scenarioAssertions = [
    ['ready', 'Ready', 'Moonraker Ready'],
    ['printing', 'Printing', 'Moonraker Printing'],
    ['paused', 'Paused', 'Moonraker Paused'],
    ['shutdown', 'Shutdown', 'Moonraker Shutdown'],
  ];
  for (const [printerId, scenario, printerName] of scenarioAssertions) {
    assert.match(moonrakerEmulatorTemplate, new RegExp(`Emulator__Scenario=${scenario}\\b`));
    assert.match(moonrakerEmulatorTemplate, new RegExp(`Emulator__PrinterId=${printerId}\\b`));
    assert.match(
      moonrakerEmulatorTemplate,
      new RegExp(`Emulator__PrinterName=${printerName.replace(/ /g, '\\s')}`),
    );
  }

  assert.match(moonrakerEmulatorTemplate, /read_only: true/);
  assert.match(moonrakerEmulatorTemplate, /cap_drop:\s*\n\s*- ALL/);
  assert.equal(
    (moonrakerEmulatorTemplate.match(/<<: \*moonraker-emulator-security/g) ?? []).length,
    moonrakerEmulatorInstances.length,
  );
  assert.equal(
    (moonrakerEmulatorTemplate.match(/healthcheck: \*moonraker-emulator-healthcheck/g) ?? [])
      .length,
    moonrakerEmulatorInstances.length,
  );
  assert.equal(
    (moonrakerEmulatorTemplate.match(/deploy: \*moonraker-emulator-deploy/g) ?? []).length,
    moonrakerEmulatorInstances.length,
  );
  assert.doesNotMatch(moonrakerEmulatorTemplate, /cap_add:/);
  assert.doesNotMatch(moonrakerEmulatorTemplate, /\/var\/run\/docker\.sock/);
  assert.doesNotMatch(moonrakerEmulatorTemplate, /^\s+ports:/m);
  assert.match(
    moonrakerEmulatorTemplate,
    /test: \["CMD", "curl", "-f", "http:\/\/localhost:7125\/healthz"\]/,
  );
  assert.match(
    moonrakerEmulatorTemplate,
    /Emulator__EnableControlApi=\$\{MOONRAKER_EMULATOR_ENABLE_CONTROL_API:-false\}/,
  );
  assert.match(
    moonrakerEmulatorTemplate,
    /Printer\.BackendUrl trims/,
  );
});
