import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { assertApprovedGolden, validateApprovalDocument, validateCatalog } from './policy.mjs';

const catalog = JSON.parse(await readFile(new URL('./catalog.json', import.meta.url), 'utf8'));
const approvals = JSON.parse(await readFile(new URL('./golden-approvals.json', import.meta.url), 'utf8'));
const candidateScenarioIds = catalog.entries
  .filter(entry => entry.disposition === 'candidate')
  .flatMap(entry => entry.scenarioIds);
const uniqueCandidateScenarioIds = [...new Set(candidateScenarioIds)];

test('every inventory row has a stable execution or ownership disposition', () => {
  assert.deepEqual(validateCatalog(catalog), []);
  const iosEntries = catalog.entries.filter(entry => entry.platform === 'ios');
  assert.equal(iosEntries.length, 37);
  assert.ok(iosEntries.every(entry =>
    entry.disposition === 'unverified' &&
    entry.reason.startsWith('Owned by separate iOS track; excluded from web coverage totals and gates.'),
  ));
  assert.ok(catalog.entries.filter(entry => entry.disposition === 'candidate').every(entry =>
    entry.platform === 'web' || entry.platform === 'site',
  ));
  assert.ok(catalog.entries.some(entry => entry.platform === 'web' && entry.disposition === 'unverified'));
  assert.ok(catalog.entries.some(entry => entry.evidenceFlags.includes('I')));
  assert.equal(new Set(catalog.entries.map(entry => entry.id)).size, catalog.entries.length);
});

test('every executable candidate has exactly one pending human approval record', () => {
  assert.deepEqual(validateApprovalDocument(approvals, catalog, uniqueCandidateScenarioIds), []);
  assert.equal(approvals.scenarios.length, uniqueCandidateScenarioIds.length);
  assert.ok(approvals.scenarios.every(scenario => scenario.status === 'pending-human-review'));
});

test('the checked-in pending approval artifact cannot authorize a golden', () => {
  for (const approval of approvals.scenarios) {
    assert.throws(
      () => assertApprovedGolden({
        approval,
        sourceSha256: catalog.source.sha256,
        renderProfileId: catalog.renderProfile.id,
        baselineExists: false,
      }),
      /pending human review/,
      approval.id,
    );
  }
});

test('catalog mutations that silently drop coverage rows are rejected', () => {
  const mutated = structuredClone(catalog);
  mutated.entries[0] = { ...mutated.entries[0], disposition: 'candidate', scenarioIds: undefined, reason: '' };
  assert.ok(validateCatalog(mutated).some(error => error.includes('candidate must name an executable scenario')));
  assert.ok(validateCatalog(mutated).some(error => error.includes('reason must explain')));
});

test('approval mutations cannot authorize stale or incomplete evidence', () => {
  const approvalDocumentWithInvalidDigest = structuredClone(approvals);
  approvalDocumentWithInvalidDigest.scenarios[0] = {
    ...approvalDocumentWithInvalidDigest.scenarios[0],
    status: 'approved',
  };
  assert.ok(validateApprovalDocument(approvalDocumentWithInvalidDigest, catalog, uniqueCandidateScenarioIds)
    .some(error => error.includes('no valid approved baseline SHA-256 digest')));

  const approved = {
    ...approvals.scenarios[0],
    status: 'approved',
    approvedBy: 'reviewer',
    approvedAt: '2026-11-08T00:00:00Z',
    approvedSourceSha: 'a'.repeat(40),
    baselineSha256: 'b'.repeat(64),
    sourceInventorySha256: catalog.source.sha256,
    renderProfileId: catalog.renderProfile.id,
  };
  assert.throws(() => assertApprovedGolden({
    approval: approved,
    sourceSha256: '0'.repeat(64),
    renderProfileId: catalog.renderProfile.id,
    baselineExists: true,
  }), /stale/);
  assert.throws(() => assertApprovedGolden({
    approval: approved,
    sourceSha256: catalog.source.sha256,
    renderProfileId: catalog.renderProfile.id,
    baselineExists: false,
  }), /missing/);
  assert.throws(() => assertApprovedGolden({
    approval: approved,
    sourceSha256: catalog.source.sha256,
    renderProfileId: catalog.renderProfile.id,
    baselineExists: true,
    baselineSha256: 'c'.repeat(64),
  }), /changed since approval/);
  assert.doesNotThrow(() => assertApprovedGolden({
    approval: approved,
    sourceSha256: catalog.source.sha256,
    renderProfileId: catalog.renderProfile.id,
    baselineExists: true,
    baselineSha256: 'b'.repeat(64),
  }));
  assert.throws(() => assertApprovedGolden({
    approval: { ...approved, baselineSha256: undefined },
    sourceSha256: catalog.source.sha256,
    renderProfileId: catalog.renderProfile.id,
    baselineExists: true,
    baselineSha256: 'b'.repeat(64),
  }), /pin the exact baseline PNG SHA-256/);
});
