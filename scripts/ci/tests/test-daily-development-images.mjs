import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const workflow = readFileSync('.github/workflows/daily-development-images.yml', 'utf8');
const dailyRegistryOverlay = readFileSync(
  'scripts/docker/compose-templates/docker-compose.daily-registry.yml',
  'utf8',
);
const validationOverlay = readFileSync(
  'scripts/docker/compose-templates/docker-compose.daily-validation.yml',
  'utf8',
);
const documentation = readFileSync('docs/DAILY_DEVELOPMENT_IMAGES.md', 'utf8');
const triggers = workflow.slice(workflow.indexOf('on:'), workflow.indexOf('concurrency:'));

const services = [
  ['api', 'api-runtime'],
  ['frontend', 'frontend-runtime'],
  ['slicer-host', 'slicer-host-runtime'],
  ['printer-discovery', 'printer-discovery-runtime'],
  ['orcaslicer-worker', 'orcaslicer-worker'],
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
  ]) {
    assert.match(workflow, new RegExp(healthPath.replace('/', '\\/')));
  }
  assert.doesNotMatch(workflow, /--entrypoint dotnet "\$IMAGE" --info/);
  assert.match(workflow, /Upload validated image archive/);
  assert.match(workflow, /Download validated image archive/);
  assert.match(workflow, /docker load --input validated-image\/image\.tar/);
  assert.equal(workflow.match(/uses: docker\/build-push-action@v7/g)?.length, 1);
  assert.match(workflow, /packages: write/);
  assert.match(workflow, /password: \$\{\{ secrets\.GITHUB_TOKEN \}\}/);
});

test('publication uses run-unique tags and emits one coherent digest manifest', () => {
  assert.match(
    workflow,
    /TAG: sha-\$\{\{ needs\.source\.outputs\.commit_sha \}\}-run-\$\{\{ github\.run_id \}\}-attempt-\$\{\{ github\.run_attempt \}\}/,
  );
  assert.doesNotMatch(workflow, /image_tag: \$\{\{ steps\.source\.outputs\.image_tag \}\}/);
  assert.doesNotMatch(workflow, /value=latest|:latest/);
  assert.doesNotMatch(workflow, /docker manifest inspect "\$reference"/);
  assert.match(workflow, /docker tag "\$VALIDATED_IMAGE" "\$reference"/);
  assert.match(workflow, /docker push "\$reference"/);
  assert.match(workflow, /Final published tag does not match the validated image/);
  assert.match(workflow, /Expected five image digest records/);
  assert.match(workflow, /name: daily-development-image-set/);
  assert.match(workflow, /retention-days: 90/);
  assert.match(workflow, /reference: \(\.image \+ "@" \+ \.digest\)/);
  assert.match(workflow, /test\("\^sha256:\[0-9a-f\]\{64\}\$"\)/);
});

test('registry and local validation overlays cover the complete stack', () => {
  for (const [service] of services) {
    assert.match(dailyRegistryOverlay, new RegExp(`^  ${service}:`, 'm'));
  }

  for (const variable of [
    'PRINTFARMER_API_IMAGE',
    'PRINTFARMER_FRONTEND_IMAGE',
    'PRINTFARMER_SLICER_HOST_IMAGE',
    'PRINTFARMER_PRINTER_DISCOVERY_IMAGE',
    'PRINTFARMER_ORCASLICER_WORKER_IMAGE',
  ]) {
    assert.match(dailyRegistryOverlay, new RegExp(variable));
  }

  assert.match(validationOverlay, /ASPNETCORE_ENVIRONMENT: Development/);
  assert.match(validationOverlay, /TestEmulator__Enabled: "true"/);
  assert.match(validationOverlay, /EnablePeriodicDiscovery: "false"/);
  assert.match(validationOverlay, /Worker__MaxConcurrentJobs: "1"/);
  assert.match(validationOverlay, /name: printfarmer-daily-validation/);
  assert.match(validationOverlay, /container_name: !reset null/);
  for (const binding of [
    '127.0.0.1:${POSTGRES_PORT:-15432}:5432',
    '127.0.0.1:${API_PORT:-15245}:5245',
    '127.0.0.1:${SLICER_HOST_PORT:-15246}:5246',
    '127.0.0.1:${HTTP_PORT:-18080}:80',
    '127.0.0.1:${HTTPS_PORT:-18443}:443',
  ]) {
    assert.match(validationOverlay, new RegExp(binding.replace(/[${}]/g, '\\$&')));
  }
  assert.match(validationOverlay, /name: printfarmer-daily-validation-network/);
  assert.match(
    documentation,
    /\.\/scripts\/generate-certs\.sh "\$STACK_DIR\/deploy\/nginx\/certs"/,
  );
  assert.match(documentation, /--project-name "\$COMPOSE_PROJECT_NAME"/);
  assert.match(documentation, /export API_PORT=15245/);
  assert.match(documentation, /export POSTGRES_USER=printfarmer/);
  assert.match(documentation, /unset POSTGRES_USER/);
});
