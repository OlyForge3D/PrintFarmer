// Self-tests for scripts/ci/check-contract-drift.mjs (issue #2243).
//
// Two kinds of coverage, deliberately kept in one file:
//   1. Unit tests against the pure functions with synthetic fixtures/allowlists
//      — fast, and exercise edge cases that would be awkward to construct in
//      the real corpus (expired exceptions, missing fields, drift itself).
//   2. An end-to-end run of the real CLI against THIS repository's actual
//      `fixtures/wire-contracts/` corpus and `scripts/ci/contract-drift-exceptions.json`
//      allowlist, asserting it exits 0 with zero findings. This is what turns
//      "zero unexplained findings" from an aspirational claim into an enforced
//      CI property: if a future PR adds a fixture family with bad casing and
//      no allowlist entry, this test fails.

import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  CAMEL_CASE_RE,
  activeAllowlistBoundaries,
  apiFamilyBoundary,
  apiKeyBoundary,
  checkAllowlistShape,
  checkCorpusNonEmpty,
  checkFixtureBoundaries,
  checkFixtureProducerCoupling,
  collectObjectKeys,
  evaluateDrift,
  nativeFamilyBoundary,
  parseFixtureFamily,
} from '../check-contract-drift.mjs';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const scriptPath = path.join(repositoryRoot, 'scripts', 'ci', 'check-contract-drift.mjs');

function validException(overrides = {}) {
  return {
    boundary: 'native-slicer:widget',
    rationale: 'A real, non-empty rationale for this boundary.',
    owner: 'squad:kane',
    expiry: '2099-01-01',
    ...overrides,
  };
}

// --- collectObjectKeys ------------------------------------------------------

test('collectObjectKeys walks nested objects and arrays, collecting only object keys', () => {
  const keys = collectObjectKeys({
    id: '1',
    tags: ['a', 'b'],
    nested: { innerKey: 1, list: [{ deepKey: 2 }] },
  });
  assert.deepEqual(new Set(keys), new Set(['id', 'tags', 'nested', 'innerKey', 'list', 'deepKey']));
});

test('collectObjectKeys returns nothing for scalar/array-of-scalar payloads', () => {
  assert.deepEqual(collectObjectKeys('just a string'), []);
  assert.deepEqual(collectObjectKeys(['a', 'b', 'c']), []);
  assert.deepEqual(collectObjectKeys(null), []);
});

// --- parseFixtureFamily / boundary helpers ----------------------------------

test('parseFixtureFamily extracts root and family from a manifest-relative path', () => {
  assert.deepEqual(parseFixtureFamily('api/slicer-profiles/profiles.populated.json'), {
    root: 'api',
    family: 'slicer-profiles',
  });
  assert.deepEqual(parseFixtureFamily('native-slicer/filament/minimal.json'), {
    root: 'native-slicer',
    family: 'filament',
  });
});

test('parseFixtureFamily rejects unknown roots and too-short paths', () => {
  assert.equal(parseFixtureFamily('manifest.json'), null);
  assert.equal(parseFixtureFamily('unexpected/family/file.json'), null);
});

test('boundary helpers produce the documented string shapes', () => {
  assert.equal(nativeFamilyBoundary('filament'), 'native-slicer:filament');
  assert.equal(apiFamilyBoundary('tasks'), 'api:tasks');
  assert.equal(apiKeyBoundary('tasks', 'task_id'), 'api:tasks#task_id');
});

test('CAMEL_CASE_RE accepts camelCase and rejects snake_case/PascalCase/kebab-case', () => {
  assert.equal(CAMEL_CASE_RE.test('maxConcurrentJobs'), true);
  assert.equal(CAMEL_CASE_RE.test('id'), true);
  assert.equal(CAMEL_CASE_RE.test('task_id'), false);
  assert.equal(CAMEL_CASE_RE.test('TaskId'), false);
  assert.equal(CAMEL_CASE_RE.test('task-id'), false);
});

// --- checkAllowlistShape -----------------------------------------------------

test('checkAllowlistShape accepts a well-formed entry', () => {
  assert.deepEqual(checkAllowlistShape([validException()], new Date('2026-01-01')), []);
});

test('checkAllowlistShape flags each missing required field', () => {
  const findings = checkAllowlistShape([{ boundary: 'api:tasks' }], new Date('2026-01-01'));
  assert.ok(findings.some((f) => f.includes("missing required field 'rationale'")));
  assert.ok(findings.some((f) => f.includes("missing required field 'owner'")));
  assert.ok(findings.some((f) => f.includes("neither 'reviewTrigger' nor 'expiry'")));
});

test('checkAllowlistShape requires at least one of reviewTrigger/expiry', () => {
  const entry = validException({ expiry: undefined });
  delete entry.expiry;
  const findings = checkAllowlistShape([entry], new Date('2026-01-01'));
  assert.ok(findings.some((f) => f.includes("neither 'reviewTrigger' nor 'expiry'")));
});

test('checkAllowlistShape accepts reviewTrigger alone with no expiry', () => {
  const entry = validException();
  delete entry.expiry;
  entry.reviewTrigger = 'Re-review when the vendor format changes.';
  assert.deepEqual(checkAllowlistShape([entry], new Date('2026-01-01')), []);
});

test('checkAllowlistShape flags an expired entry', () => {
  const findings = checkAllowlistShape([validException({ expiry: '2020-01-01' })], new Date('2026-01-01'));
  assert.ok(findings.some((f) => f.includes('expired on 2020-01-01')));
});

test('checkAllowlistShape flags an unparsable expiry', () => {
  const findings = checkAllowlistShape([validException({ expiry: 'not-a-date' })], new Date('2026-01-01'));
  assert.ok(findings.some((f) => f.includes('unparsable')));
});

test('checkAllowlistShape flags a duplicate boundary', () => {
  const findings = checkAllowlistShape([validException(), validException()], new Date('2026-01-01'));
  assert.ok(findings.some((f) => f.includes("duplicate boundary 'native-slicer:widget'")));
});

// --- activeAllowlistBoundaries -----------------------------------------------
// Bishop review (dev/jpapiez/contract-drift-gate @ 51aa7ee2): an expired entry
// must stop suppressing the boundary it names, not just gain a second
// "expired" finding alongside a suppressed original one.

test('activeAllowlistBoundaries includes a well-formed, unexpired entry', () => {
  const boundaries = activeAllowlistBoundaries([validException()], new Date('2026-01-01'));
  assert.ok(boundaries.has('native-slicer:widget'));
});

test('activeAllowlistBoundaries excludes an expired entry', () => {
  const boundaries = activeAllowlistBoundaries([validException({ expiry: '2020-01-01' })], new Date('2026-01-01'));
  assert.ok(!boundaries.has('native-slicer:widget'));
});

test('activeAllowlistBoundaries excludes an entry with an unparsable expiry', () => {
  const boundaries = activeAllowlistBoundaries([validException({ expiry: 'not-a-date' })], new Date('2026-01-01'));
  assert.ok(!boundaries.has('native-slicer:widget'));
});

test('activeAllowlistBoundaries excludes an entry missing a required field', () => {
  const entry = validException();
  delete entry.rationale;
  const boundaries = activeAllowlistBoundaries([entry], new Date('2026-01-01'));
  assert.ok(!boundaries.has('native-slicer:widget'));
});

test('an expired allowlist entry stops suppressing the drift it named (does not silently keep passing)', () => {
  // Regression for the exact bug Bishop's review caught: evaluateDrift used to
  // pass every allowlist boundary through to checkFixtureBoundaries regardless
  // of expiry, so an expired exception kept suppressing the underlying finding
  // forever — only a separate "expired" finding was ever visible.
  const result = evaluateDrift({
    allowlist: [validException({ expiry: '2020-01-01' })],
    fixtures: new Map([['native-slicer/widget/a.json', { snake_case: 1 }]]),
    changedPaths: [],
    now: new Date('2026-01-01'),
  });
  assert.equal(result.ok, false);
  assert.ok(result.findings.some((f) => f.includes('expired on 2020-01-01')));
  assert.ok(
    result.findings.some((f) => f.includes("native-slicer fixture family 'widget'")),
    'the underlying native-boundary finding must reappear once the exception has expired',
  );
});

// --- checkCorpusNonEmpty ------------------------------------------------------
// Hicks review (dev/jpapiez/contract-drift-gate @ 51aa7ee2): a misconfigured
// corpus path must not silently report "0 fixtures checked, 0 findings" as a
// clean pass.

test('checkCorpusNonEmpty flags an empty corpus', () => {
  const findings = checkCorpusNonEmpty(new Map());
  assert.equal(findings.length, 1);
  assert.match(findings[0], /No payload fixtures were found/);
});

test('checkCorpusNonEmpty passes once at least one fixture is present', () => {
  assert.deepEqual(checkCorpusNonEmpty(new Map([['api/tasks/tasks.populated.json', { id: '1' }]])), []);
});

// --- checkFixtureBoundaries --------------------------------------------------

test('checkFixtureBoundaries passes camelCase api fixtures with no allowlist needed', () => {
  const fixtures = new Map([['api/tasks/tasks.populated.json', { id: '1', dueDate: '2026-01-01' }]]);
  assert.deepEqual(checkFixtureBoundaries(fixtures, new Set()), []);
});

test('checkFixtureBoundaries flags a snake_case key under api/ with no allowlist entry', () => {
  const fixtures = new Map([['api/tasks/tasks.populated.json', { task_id: '1' }]]);
  const findings = checkFixtureBoundaries(fixtures, new Set());
  assert.equal(findings.length, 1);
  assert.match(findings[0], /non-camelCase property 'task_id'/);
  assert.match(findings[0], /api:tasks#task_id/);
});

test('checkFixtureBoundaries is satisfied by a specific key-level allowlist entry', () => {
  const fixtures = new Map([['api/tasks/tasks.populated.json', { task_id: '1' }]]);
  const findings = checkFixtureBoundaries(fixtures, new Set(['api:tasks#task_id']));
  assert.deepEqual(findings, []);
});

test('checkFixtureBoundaries is satisfied by a family-level allowlist entry', () => {
  const fixtures = new Map([['api/tasks/tasks.populated.json', { task_id: '1', another_bad_key: '2' }]]);
  const findings = checkFixtureBoundaries(fixtures, new Set(['api:tasks']));
  assert.deepEqual(findings, []);
});

test('checkFixtureBoundaries flags an unallowlisted native-slicer family exactly once regardless of key count', () => {
  const fixtures = new Map([
    ['native-slicer/filament/a.json', { compatible_printers: [], filament_type: 'PLA' }],
    ['native-slicer/filament/b.json', { compatible_printers: [], setting_id: 'x' }],
  ]);
  const findings = checkFixtureBoundaries(fixtures, new Set());
  assert.equal(findings.length, 1);
  assert.match(findings[0], /native-slicer:filament/);
});

test('checkFixtureBoundaries accepts an allowlisted native-slicer family', () => {
  const fixtures = new Map([['native-slicer/filament/a.json', { compatible_printers: [] }]]);
  assert.deepEqual(checkFixtureBoundaries(fixtures, new Set(['native-slicer:filament'])), []);
});

test('checkFixtureBoundaries ignores non-payload paths (manifest/README already filtered by caller, but unknown roots too)', () => {
  const fixtures = new Map([['docs/some-note.json', { snake_case_key: 1 }]]);
  assert.deepEqual(checkFixtureBoundaries(fixtures, new Set()), []);
});

// --- checkFixtureProducerCoupling -------------------------------------------

test('checkFixtureProducerCoupling passes when no fixture changed', () => {
  assert.deepEqual(checkFixtureProducerCoupling(['src/Web/ReactApp/src/App.tsx']), []);
});

test('checkFixtureProducerCoupling flags a fixture-only edit (the #2232 shape)', () => {
  const findings = checkFixtureProducerCoupling(['fixtures/wire-contracts/api/tasks/tasks.populated.json']);
  assert.equal(findings.length, 1);
  assert.match(findings[0], /#2232 regression shape/);
  assert.match(findings[0], /tasks\.populated\.json/);
});

test('checkFixtureProducerCoupling passes when a backend source file changed alongside', () => {
  const findings = checkFixtureProducerCoupling([
    'fixtures/wire-contracts/api/tasks/tasks.populated.json',
    'src/api/Controllers/TasksController.cs',
  ]);
  assert.deepEqual(findings, []);
});

test('checkFixtureProducerCoupling passes when a .NET test file changed alongside (regeneration path)', () => {
  const findings = checkFixtureProducerCoupling([
    'fixtures/wire-contracts/api/tasks/tasks.populated.json',
    'src/tests/Farm.Web.Api.Tests/Contracts/TasksContractTests.cs',
  ]);
  assert.deepEqual(findings, []);
});

test('checkFixtureProducerCoupling is NOT satisfied by a consumer-only change (React/iOS)', () => {
  const findings = checkFixtureProducerCoupling([
    'fixtures/wire-contracts/api/tasks/tasks.populated.json',
    'src/Web/ReactApp/src/types/api.ts',
    'mobile/PrintFarmer/Models/TaskModel.swift',
  ]);
  assert.equal(findings.length, 1);
});

test('checkFixtureProducerCoupling ignores manifest.json/README.md-only changes as non-payload', () => {
  assert.deepEqual(
    checkFixtureProducerCoupling(['fixtures/wire-contracts/manifest.json', 'fixtures/wire-contracts/README.md']),
    [],
  );
});

// --- evaluateDrift (composition) --------------------------------------------

test('evaluateDrift is ok:true and empty findings for a fully clean input', () => {
  const result = evaluateDrift({
    allowlist: [validException()],
    fixtures: new Map([
      ['api/tasks/tasks.populated.json', { id: '1' }],
      ['native-slicer/widget/a.json', { snake_case: 1 }],
    ]),
    changedPaths: ['src/api/Program.cs'],
    now: new Date('2026-01-01'),
  });
  assert.deepEqual(result, { findings: [], ok: true });
});

test('evaluateDrift aggregates findings from all three checks', () => {
  const result = evaluateDrift({
    allowlist: [{ boundary: 'x' }], // missing fields -> allowlist-shape finding
    fixtures: new Map([
      ['api/tasks/tasks.populated.json', { task_id: '1' }], // casing finding
      ['native-slicer/filament/a.json', { compatible_printers: [] }], // unallowlisted native finding
    ]),
    changedPaths: ['fixtures/wire-contracts/api/tasks/tasks.populated.json'], // producer-coupling finding
    now: new Date('2026-01-01'),
  });
  assert.equal(result.ok, false);
  assert.ok(result.findings.length >= 4);
});

// --- CLI end-to-end ----------------------------------------------------------

test('CLI exits 0 with zero findings against this repository\'s real corpus and allowlist', () => {
  const output = execFileSync('node', [scriptPath], { cwd: repositoryRoot, encoding: 'utf8' });
  assert.match(output, /OK — \d+ fixture\(s\) checked, 0 unexplained findings\./);
});

test('CLI reads a changed-file path and fails on an injected fixture-only edit, naming the actual finding', async () => {
  const tempDir = await mkdtemp(path.join(tmpdir(), 'contract-drift-cli-'));
  try {
    const changedFile = path.join(tempDir, 'changed.z');
    await writeFile(changedFile, 'fixtures/wire-contracts/api/tasks/tasks.populated.json\0');
    let threw = false;
    try {
      execFileSync('node', [scriptPath, '--changed-file', changedFile], {
        cwd: repositoryRoot,
        encoding: 'utf8',
      });
    } catch (err) {
      threw = true;
      // Hicks review (dev/jpapiez/contract-drift-gate @ 51aa7ee2): a bare
      // "the command failed" assertion would also pass if the CLI failed for
      // an unrelated reason (a thrown exception, a crash, wrong exit code from
      // a different check). Assert the actual producer-coupling finding is
      // the one that fired.
      assert.equal(err.status, 1);
      assert.match(err.stderr, /#2232 regression shape/);
      assert.match(err.stderr, /tasks\.populated\.json/);
    }
    assert.ok(threw, 'expected the CLI to exit non-zero for a fixture-only edit with no producer-side change');
  } finally {
    await rm(tempDir, { recursive: true, force: true });
  }
});

test('CLI passes the same injected diff once a producer-side file is also listed', async () => {
  const tempDir = await mkdtemp(path.join(tmpdir(), 'contract-drift-cli-'));
  try {
    const changedFile = path.join(tempDir, 'changed.z');
    await writeFile(
      changedFile,
      'fixtures/wire-contracts/api/tasks/tasks.populated.json\0src/api/Controllers/TasksController.cs\0',
    );
    const output = execFileSync('node', [scriptPath, '--changed-file', changedFile], {
      cwd: repositoryRoot,
      encoding: 'utf8',
    });
    assert.match(output, /OK —/);
  } finally {
    await rm(tempDir, { recursive: true, force: true });
  }
});

test('CLI tolerates a missing changed-file path (local ad-hoc run) without failing', () => {
  const output = execFileSync('node', [scriptPath, '--changed-file', '/does/not/exist.z'], {
    cwd: repositoryRoot,
    encoding: 'utf8',
  });
  assert.match(output, /OK —/);
});
