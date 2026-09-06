import assert from 'node:assert/strict';
import test from 'node:test';
import {
  epicLabel,
  draftChildPlanMarker,
  emptyChildPlanMarker,
  evaluateEpicDependencies,
  exitCodeFor,
  flatGraphMarker,
  formatGateComment,
  gateCommentMarker,
  parseEpicDeclarations,
  parsePositiveSafeInteger,
} from '../verify-epic-dependencies.mjs';

function epic(overrides = {}) {
  return {
    number: 2410,
    labels: [{ name: epicLabel }],
    body: draftChildPlanMarker,
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

test('an explicitly draft epic without linked children passes without inventing edges', () => {
  const result = evaluateEpicDependencies({ issue: epic() });
  assert.equal(result.classification, 'PASS');
  assert.equal(result.childCount, 0);
  assert.equal(result.childPlanState, 'draft');
  assert.match(result.reason, /completeness is not verified/);
});

test('a finalized plan with zero native children fails, even with a flat opt-out', () => {
  for (const graphMarker of ['', flatGraphMarker]) {
    const result = evaluateEpicDependencies({
      issue: epic({
        body: `<!-- epic-child-plan: finalized #11 #12 -->\n${graphMarker}`,
      }),
    });
    assert.equal(result.classification, 'FAIL');
    assert.equal(exitCodeFor(result.classification), 2);
    assert.equal(result.childPlanState, 'finalized');
    assert.equal(result.childCount, 0);
    assert.deepEqual(result.missingChildren, [11, 12]);
    assert.match(result.reason, /not linked sub-issues: #11, #12/);
  }
});

test('a finalized plan fails when only a valid connected subset is linked', () => {
  const result = evaluateEpicDependencies({
    issue: epic({ body: '<!-- epic-child-plan: finalized #11, #12, #13 -->' }),
    children: children(11, 12),
    edges: [{ blocker: 11, blocked: 12 }],
  });
  assert.equal(result.classification, 'FAIL');
  assert.equal(result.edgeCount, 1);
  assert.deepEqual(result.missingChildren, [13]);
  assert.match(result.reason, /not linked sub-issues: #13/);
});

test('a complete finalized plan ignores contextual issue references', () => {
  const result = evaluateEpicDependencies({
    issue: epic({
      body: 'Follow-up to #99; related #100.\n' +
        '<!-- epic-child-plan: finalized #11, #12 -->',
    }),
    children: children(11, 12),
    edges: [{ blocker: 11, blocked: 12 }],
  });
  assert.equal(result.classification, 'PASS');
  assert.deepEqual(result.declaredChildren, [11, 12]);
  assert.deepEqual(result.missingChildren, []);
  assert.match(result.reason, /All 2 declared children are linked/);
});

test('a complete finalized flat plan passes without inventing edges', () => {
  const result = evaluateEpicDependencies({
    issue: epic({
      body: `<!-- epic-child-plan: finalized #11 #12 -->\n${flatGraphMarker}`,
    }),
    children: children(11, 12),
  });
  assert.equal(result.classification, 'PASS');
  assert.equal(result.edgeCount, 0);
});

test('explicit draft and empty plans ignore contextual references with zero links', () => {
  for (const marker of [draftChildPlanMarker, emptyChildPlanMarker]) {
    const result = evaluateEpicDependencies({
      issue: epic({ body: `${marker}\nBackground #99; possible work #100.` }),
    });
    assert.equal(result.classification, 'PASS', marker);
    assert.deepEqual(result.declaredChildren, []);
    assert.deepEqual(result.missingChildren, []);
  }
});

test('an explicitly empty plan cannot hide linked children', () => {
  const result = evaluateEpicDependencies({
    issue: epic({ body: emptyChildPlanMarker }),
    children: children(11, 12),
    edges: [{ blocker: 11, blocked: 12 }],
  });
  assert.equal(result.classification, 'FAIL');
  assert.match(result.reason, /empty epic but linked sub-issues exist/);
});

test('missing child-plan declarations fail instead of implying draft or completeness', () => {
  for (const linkedChildren of [[], children(11, 12)]) {
    const result = evaluateEpicDependencies({
      issue: epic({ body: 'Background #99; no machine-readable plan.' }),
      children: linkedChildren,
      edges: [{ blocker: 11, blocked: 12 }],
    });
    assert.equal(result.classification, 'FAIL');
    assert.equal(result.childPlanState, 'unspecified');
    assert.match(result.reason, /Exactly one child-plan declaration is required/);
  }
});

test('malformed, duplicate and conflicting child-plan declarations fail closed', () => {
  for (const body of [
    '<!-- epic-child-plan: finalized -->',
    '<!-- epic-child-plan: final #11 -->',
    '<!-- epic-child-plan: draft #11 -->',
    '<!-- epic-child-plan: empty #11 -->',
    '<!-- epic-child-plan: finalized #0 -->',
    '<!-- epic-child-plan: finalized #9007199254740992 -->',
    '<!-- epic-child-plan: finalized #11 #11 -->',
    '<!-- epic-child-plan: finalized #11',
    'epic-child-plan: finalized #11 -->',
    `${draftChildPlanMarker}\n${draftChildPlanMarker}`,
    `${draftChildPlanMarker}\n<!-- epic-child-plan: finalized #11 -->`,
    `${emptyChildPlanMarker}\n<!-- epic-child-plan: finalized #11 -->`,
  ]) {
    const result = evaluateEpicDependencies({
      issue: epic({ body }),
      children: children(11, 12),
      edges: [{ blocker: 11, blocked: 12 }],
    });
    assert.equal(result.classification, 'FAIL', body);
    assert.match(result.reason, /child-plan|Child-plan/, body);
  }
});

test('fenced child-plan examples do not declare children or mask missing declarations', () => {
  const example = '```text\n<!-- epic-child-plan: finalized #99 -->\n```';
  const draft = evaluateEpicDependencies({
    issue: epic({ body: `${draftChildPlanMarker}\n${example}` }),
  });
  assert.equal(draft.classification, 'PASS');
  assert.deepEqual(draft.declaredChildren, []);
  const missing = evaluateEpicDependencies({ issue: epic({ body: example }) });
  assert.equal(missing.classification, 'FAIL');
  assert.match(missing.reason, /Exactly one child-plan declaration is required/);
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
    issue: epic({ body: `${draftChildPlanMarker}\n${flatGraphMarker}` }),
    children: children(11, 12),
  });
  assert.equal(result.classification, 'PASS');
  assert.equal(result.flatGraph, true);
});

test('a flat declaration fails when dependency edges exist', () => {
  const result = evaluateEpicDependencies({
    issue: epic({ body: `${draftChildPlanMarker}\n${flatGraphMarker}` }),
    children: children(11, 12),
    edges: [{ blocker: 11, blocked: 12 }],
  });
  assert.equal(result.classification, 'FAIL');
  assert.match(result.reason, /declares a flat graph but has/);
});

test('declared first-wave children may be isolated in an otherwise real graph', () => {
  const result = evaluateEpicDependencies({
    issue: epic({ body: `${draftChildPlanMarker}\n<!-- epic-first-wave: #11 -->` }),
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
    'epic-first-wave: #11 -->',
    'epic-dependencies: flat',
  ]) {
    const result = evaluateEpicDependencies({
      issue: epic({ body }),
      children: children(11, 12),
      edges: [{ blocker: 11, blocked: 12 }],
    });
    assert.equal(result.classification, 'FAIL', body);
    assert.match(result.reason, /malformed|must use/, body);
  }
});

test('declaration examples inside fenced code blocks are not binding', () => {
  const result = evaluateEpicDependencies({
    issue: epic({
      body: [
        draftChildPlanMarker,
        'Example only:',
        '```text',
        '<!-- epic-dependencies: flat -->',
        '```',
      ].join('\n'),
    }),
    children: children(11, 12),
    edges: [{ blocker: 11, blocked: 12 }],
  });
  assert.equal(result.classification, 'PASS');
  assert.equal(result.flatGraph, false);
});

test('mixed fence delimiters cannot expose a marker inside an open fence', () => {
  const result = evaluateEpicDependencies({
    issue: epic({
      body: [
        '```text',
        '~~~',
        '<!-- epic-dependencies: flat -->',
      ].join('\n'),
    }),
    children: children(11, 12),
  });
  assert.equal(result.classification, 'FAIL');
  assert.equal(result.flatGraph, false);
  assert.match(result.reason, /zero internal dependency edges/);
});

test('issue numbers must be positive safe integers', () => {
  assert.equal(parsePositiveSafeInteger('2410', '--issue'), 2410);
  for (const value of ['0', '-1', '1.5', '9007199254740992', '1e3', '']) {
    assert.throws(
      () => parsePositiveSafeInteger(value, '--issue'),
      /positive safe integer/,
      value,
    );
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
  assert.match(comment, /\| Child-plan state \| draft \|/);
});

test('the workflow comment identifies a finalized plan with missing native links', () => {
  const result = evaluateEpicDependencies({
    issue: epic({ body: '<!-- epic-child-plan: finalized #11 #12 -->' }),
  });
  const comment = formatGateComment(result, 2410, 'https://example.com/run');
  assert.ok(comment.startsWith(gateCommentMarker));
  assert.match(comment, /Epic dependency gate: FAIL/);
  assert.match(comment, /\| Child-plan state \| finalized \|/);
  assert.match(comment, /\| Declared children \| #11, #12 \|/);
  assert.match(comment, /\| Missing native child links \| #11, #12 \|/);
});

test('workflow API-read failures remain formattable without child-plan metadata', () => {
  const comment = formatGateComment({
    classification: 'FAIL',
    reason: 'Could not read the complete epic graph.',
    childCount: 0,
    edgeCount: 0,
    flatGraph: false,
    firstWave: [],
    isolatedChildren: [],
  }, 2410, 'https://example.com/run');
  assert.match(comment, /Epic dependency gate: FAIL/);
  assert.match(comment, /Could not read the complete epic graph/);
});
