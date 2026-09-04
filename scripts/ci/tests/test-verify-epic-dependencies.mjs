import assert from 'node:assert/strict';
import test from 'node:test';
import {
  epicLabel,
  evaluateEpicDependencies,
  exitCodeFor,
  flatGraphMarker,
  formatGateComment,
  gateCommentMarker,
  parseEpicDeclarations,
} from '../verify-epic-dependencies.mjs';

function epic(overrides = {}) {
  return {
    number: 2410,
    labels: [{ name: epicLabel }],
    body: '',
    ...overrides,
  };
}

function children(...numbers) {
  return numbers.map((number) => ({ number }));
}

test('non-epics are not applicable', () => {
  const result = evaluateEpicDependencies({
    issue: epic({ labels: [] }),
    children: children(1, 2),
  });
  assert.equal(result.classification, 'NOT_APPLICABLE');
  assert.equal(exitCodeFor(result.classification), 4);
});

test('an epic without linked children passes without inventing edges', () => {
  const result = evaluateEpicDependencies({ issue: epic() });
  assert.equal(result.classification, 'PASS');
  assert.equal(result.childCount, 0);
});

test('a connected dependency graph passes and deduplicates both API directions', () => {
  const result = evaluateEpicDependencies({
    issue: epic(),
    children: children(11, 12, 13),
    edges: [
      { blocker: 11, blocked: 12 },
      { blocker: 11, blocked: 12 },
      { blocker: 12, blocked: 13 },
      { blocker: 99, blocked: 11 },
    ],
  });
  assert.equal(result.classification, 'PASS');
  assert.equal(result.edgeCount, 2);
  assert.deepEqual(result.isolatedChildren, []);
});

test('a graph-less epic fails unless it explicitly opts out', () => {
  const result = evaluateEpicDependencies({
    issue: epic(),
    children: children(11, 12),
  });
  assert.equal(result.classification, 'FAIL');
  assert.match(result.reason, /zero internal dependency edges/);
  assert.equal(exitCodeFor(result.classification), 2);
});

test('the exact flat-graph marker permits a genuinely flat epic', () => {
  const result = evaluateEpicDependencies({
    issue: epic({ body: flatGraphMarker }),
    children: children(11, 12),
  });
  assert.equal(result.classification, 'PASS');
  assert.equal(result.flatGraph, true);
});

test('a flat declaration fails when dependency edges exist', () => {
  const result = evaluateEpicDependencies({
    issue: epic({ body: flatGraphMarker }),
    children: children(11, 12),
    edges: [{ blocker: 11, blocked: 12 }],
  });
  assert.equal(result.classification, 'FAIL');
  assert.match(result.reason, /declares a flat graph but has/);
});

test('declared first-wave children may be isolated in an otherwise real graph', () => {
  const result = evaluateEpicDependencies({
    issue: epic({ body: '<!-- epic-first-wave: #11 -->' }),
    children: children(11, 12, 13),
    edges: [{ blocker: 12, blocked: 13 }],
  });
  assert.equal(result.classification, 'PASS');
  assert.deepEqual(result.firstWave, [11]);
});

test('undeclared isolated children fail with actionable issue numbers', () => {
  const result = evaluateEpicDependencies({
    issue: epic(),
    children: children(11, 12, 13),
    edges: [{ blocker: 11, blocked: 12 }],
  });
  assert.equal(result.classification, 'FAIL');
  assert.deepEqual(result.isolatedChildren, [13]);
  assert.match(result.reason, /#13/);
});

test('malformed and unknown first-wave declarations fail closed', () => {
  const malformed = parseEpicDeclarations(
    '<!-- epic-first-wave: issue 11 -->',
  );
  assert.match(malformed.errors[0], /malformed/);

  const unknown = evaluateEpicDependencies({
    issue: epic({ body: '<!-- epic-first-wave: #99 -->' }),
    children: children(11, 12),
    edges: [{ blocker: 11, blocked: 12 }],
  });
  assert.equal(unknown.classification, 'FAIL');
  assert.match(unknown.reason, /not linked sub-issues: #99/);
});

test('marker-like declaration typos and unterminated comments fail closed', () => {
  for (const body of [
    '<!-- epic-first-wave #11 -->',
    '<!-- epic-dependencies: flatt -->',
    '<!-- epic-first-wave: #11',
  ]) {
    const result = evaluateEpicDependencies({
      issue: epic({ body }),
      children: children(11, 12),
      edges: [{ blocker: 11, blocked: 12 }],
    });
    assert.equal(result.classification, 'FAIL', body);
    assert.match(result.reason, /malformed/, body);
  }
});

test('the workflow comment is marker-bound and reports graph counts', () => {
  const result = evaluateEpicDependencies({
    issue: epic(),
    children: children(11, 12),
    edges: [{ blocker: 11, blocked: 12 }],
  });
  const comment = formatGateComment(
    result,
    2410,
    'https://github.com/OlyForge3D/PrintFarmer/actions/runs/1',
  );
  assert.ok(comment.startsWith(gateCommentMarker));
  assert.match(comment, /Epic dependency gate: PASS/);
  assert.match(comment, /\| Internal dependency edges \| 1 \|/);
});
