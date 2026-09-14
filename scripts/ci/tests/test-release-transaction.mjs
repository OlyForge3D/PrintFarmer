import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { load } from 'js-yaml';
import {
  selectTransaction, validateQualificationReceipt, validateTransaction, writeRehearsalReceipt,
} from '../release-transaction.mjs';

const sha = 'a'.repeat(40);
const base = {
  GITHUB_REPOSITORY: 'OlyForge3D/PrintFarmer',
  GITHUB_EVENT_NAME: 'workflow_dispatch',
  GITHUB_REF: 'refs/heads/development',
  GITHUB_SHA: sha,
  GITHUB_WORKFLOW_SHA: sha,
  GITHUB_WORKFLOW_REF:
    'OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/development',
  GITHUB_RUN_ID: '42',
  GITHUB_RUN_ATTEMPT: '1',
  RELEASE_CHANNEL: 'insider',
  RELEASE_MODE: 'release',
  RELEASE_APPROVAL_MODE: 'single-maintainer',
};

test('stable and insider select blank or explicit canonical HEAD exactly once', () => {
  for (const channel of ['stable', 'insider']) {
    const branch = channel === 'stable' ? 'main' : 'development';
    for (const requested of ['', sha]) {
      const transaction = selectTransaction({
        ...base,
        GITHUB_REF: `refs/heads/${branch}`,
        GITHUB_WORKFLOW_REF:
          `OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/${branch}`,
        RELEASE_CHANNEL: channel,
        RELEASE_SOURCE_SHA: requested,
      });
      assert.equal(transaction.sourceCommit, sha);
      assert.equal(transaction.observedBranchHead, sha);
      assert.equal(transaction.sourceBranch, branch);
    }
  }
});

test('selection rejects wrong refs, malformed or silently substituted explicit sources and reruns', () => {
  for (const overrides of [
    { GITHUB_REF: 'refs/heads/main' },
    { RELEASE_SOURCE_SHA: 'bad' },
    { RELEASE_SOURCE_SHA: 'b'.repeat(40) },
    { GITHUB_RUN_ATTEMPT: '2' },
    { RELEASE_MODE: 'dry-run' },
    { GITHUB_WORKFLOW_SHA: 'b'.repeat(40) },
  ]) assert.throws(() => selectTransaction({ ...base, ...overrides }));
});

test('qualification and rehearsal receipts are closed, transaction-bound variants', () => {
  const transaction = selectTransaction({ ...base, RELEASE_MODE: 'rehearsal' });
  const qualification = {
    kind: 'release-qualification',
    schema: 1,
    transaction,
    checkedAt: '2026-09-13T20:00:00.000Z',
    checks: [
      'CI tooling tests', '.NET build', 'Frontend build & tests', 'squad/pre-pr-verdict',
      'path-casing', 'Build (iOS)', 'Contract drift gate',
    ],
  };
  assert.equal(validateQualificationReceipt(qualification, transaction, 'rehearsal'), qualification);
  const receipt = writeRehearsalReceipt(transaction, qualification);
  assert.equal(receipt.kind, 'release-rehearsal-only');
  assert.equal(receipt.publicationAuthorized, false);
  assert.throws(() => validateQualificationReceipt(receipt, transaction, 'release'),
    /cannot authorize release|qualification receipt/);
  assert.throws(() => validateTransaction({ ...transaction, future: true }));
});

test('single operator workflow owns release dispatch and one transaction approval', () => {
  const files = [
    '.github/workflows/consolidated-release.yml',
    '.github/workflows/ci.yml',
    '.github/workflows/qualify-canonical-release.yml',
    '.github/workflows/release-protection-rehearsal.yml',
  ];
  const workflows = Object.fromEntries(files.map(file => [file, load(readFileSync(file, 'utf8'))]));
  assert.deepEqual(Object.keys(workflows[files[0]].on).sort(), ['schedule', 'workflow_dispatch']);
  for (const file of files.slice(1)) {
    assert.equal(Object.hasOwn(workflows[file].on, 'workflow_dispatch'), false, file);
  }
  const authority = workflows[files[0]];
  assert.deepEqual(Object.keys(authority.on.workflow_dispatch.inputs), ['channel', 'source_sha', 'mode']);
  const environments = Object.values(authority.jobs).filter(job => job.environment);
  assert.equal(environments.length, 2);
  assert.ok(environments.every(job => job.if));
  assert.notEqual(environments[0].if, environments[1].if);
  assert.match(readFileSync(files[0], 'utf8'), /needs: \[admit, qualification, collect-qualification\]/);
});

test('rehearsal branch has no production credential or publisher path', () => {
  const workflow = load(readFileSync('.github/workflows/consolidated-release.yml', 'utf8'));
  const rehearsal = JSON.stringify(workflow.jobs.rehearsal);
  assert.doesNotMatch(rehearsal,
    /RELEASE_PUBLISHER_PRIVATE_KEY|RELEASE_REGISTRY_TOKEN|REGISTRY_TOKEN|id-token|docker-publish|release-control\.mjs/);
  assert.match(rehearsal, /release-transaction\.mjs rehearse/);
  assert.equal(workflow.jobs.publish.if, "inputs.mode == 'release'");
});

test('publisher jobs use credential-only environments after the single transaction approval', () => {
  const publisher = load(readFileSync('.github/workflows/docker-publish.yml', 'utf8'));
  const environments = Object.values(publisher.jobs)
    .filter(job => job.environment)
    .map(job => job.environment);
  assert.ok(environments.length > 0);
  assert.ok(environments.every(environment =>
    environment === 'release-publisher-${{ fromJSON(inputs.identity).channel }}'));
  assert.ok(environments.every(environment => !/^release-\$\{\{/.test(environment)));
});
