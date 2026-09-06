#!/usr/bin/env node

// Verifies that an epic's prose plan is backed by GitHub's machine-readable
// sub-issue and dependency APIs.
//
// This verifier FAILS CLOSED for malformed declarations, incomplete API data,
// zero-edge graphs, and undeclared isolated children. A false failure costs a
// maintainer a correction or explicit flat-graph declaration; a false pass can
// send an agent into work whose prerequisites have not landed, which is the
// exact failure this gate exists to prevent.

import { execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';

export const epicLabel = 'type:epic';
export const flatGraphMarker = '<!-- epic-dependencies: flat -->';
export const firstWaveExample = '<!-- epic-first-wave: #123 #124 -->';
export const childPlanExample = '<!-- epic-child-plan: finalized #123 #124 -->';
export const draftChildPlanMarker = '<!-- epic-child-plan: draft -->';
export const emptyChildPlanMarker = '<!-- epic-child-plan: empty -->';
export const gateCommentMarker = '<!-- epic-dependency-gate -->';

function issueNumber(value) {
  return Number.isInteger(value) && value > 0 ? value : undefined;
}

function labelName(label) {
  return typeof label === 'string' ? label : label?.name;
}

export function parsePositiveSafeInteger(value, name) {
  const text = String(value ?? '');
  const number = Number(text);
  if (!/^[1-9]\d*$/.test(text) || !Number.isSafeInteger(number)) {
    throw new Error(`${name} must be a positive safe integer.`);
  }
  return number;
}

export function hasEpicLabel(labels = []) {
  return labels.some((label) =>
    labelName(label)?.trim().toLowerCase() === epicLabel);
}

function stripFencedBlocks(body) {
  const visibleLines = [];
  let fence = undefined;
  for (const line of body.split('\n')) {
    if (fence === undefined) {
      const opening = /^[ \t]*(?<delimiter>`{3,}|~{3,})/.exec(line);
      if (opening) {
        fence = {
          character: opening.groups.delimiter[0],
          length: opening.groups.delimiter.length,
        };
      } else {
        visibleLines.push(line);
      }
      continue;
    }

    const closing = /^[ \t]*(?<delimiter>`{3,}|~{3,})[ \t]*$/.exec(line);
    if (
      closing &&
      closing.groups.delimiter[0] === fence.character &&
      closing.groups.delimiter.length >= fence.length
    ) {
      fence = undefined;
    }
  }
  return visibleLines.join('\n');
}

export function parseEpicDeclarations(body = '') {
  const errors = [];
  const declarationBody = stripFencedBlocks(body);
  const declarationComments = [
    ...declarationBody.matchAll(/<!--[\s\S]*?(?:-->|$)/g),
  ].map((match) => match[0].trim());
  const outsideComments = declarationBody.replace(/<!--[\s\S]*?(?:-->|$)/g, '');
  if (/\bepic-(?:dependencies|first-wave|child-plan)\b/i.test(outsideComments)) {
    errors.push(
      'Epic dependency declarations must use the documented HTML comment markers.',
    );
  }
  const flatDeclarations = declarationComments.filter((comment) =>
    /epic-dependencies/i.test(comment));
  const validFlatDeclarations = flatDeclarations.filter((comment) =>
    comment === flatGraphMarker);
  if (flatDeclarations.length > 1) {
    errors.push('The flat-graph opt-out marker appears more than once.');
  }
  if (flatDeclarations.length !== validFlatDeclarations.length) {
    errors.push(
      `The flat-graph declaration is malformed; use ${flatGraphMarker}.`,
    );
  }

  const firstWaveDeclarations = declarationComments.filter((comment) =>
    /epic-first-wave/i.test(comment));
  if (firstWaveDeclarations.length > 1) {
    errors.push('The first-wave declaration appears more than once.');
  }

  const firstWave = [];
  if (firstWaveDeclarations.length === 1) {
    const match = /^<!-- epic-first-wave: (#[1-9]\d*(?:[\s,]+#[1-9]\d*)*) -->$/
      .exec(firstWaveDeclarations[0]);
    if (!match) {
      errors.push(
        `The first-wave declaration is malformed; use ${firstWaveExample}.`,
      );
    } else {
      const references = [...match[1].matchAll(/#([1-9]\d*)/g)];
      for (const reference of references) {
        const number = Number.parseInt(reference[1], 10);
        if (firstWave.includes(number)) {
          errors.push(`The first-wave declaration repeats #${number}.`);
        } else {
          firstWave.push(number);
        }
      }
    }
  }

  const childPlanDeclarations = declarationComments.filter((comment) =>
    /epic-child-plan/i.test(comment));
  let childPlanState = 'unspecified';
  const declaredChildren = [];
  if (childPlanDeclarations.length !== 1) {
    errors.push(
      'Exactly one child-plan declaration is required; use ' +
      `${childPlanExample}, ${draftChildPlanMarker}, or ${emptyChildPlanMarker}.`,
    );
  } else {
    const declaration = childPlanDeclarations[0];
    if (declaration === draftChildPlanMarker) {
      childPlanState = 'draft';
    } else if (declaration === emptyChildPlanMarker) {
      childPlanState = 'empty';
    } else {
      const match = /^<!-- epic-child-plan: finalized (#[1-9]\d*(?:[\s,]+#[1-9]\d*)*) -->$/
        .exec(declaration);
      if (!match) {
        errors.push(`The child-plan declaration is malformed; use ${childPlanExample}, ` +
          `${draftChildPlanMarker}, or ${emptyChildPlanMarker}.`);
      } else {
        childPlanState = 'finalized';
        for (const reference of match[1].matchAll(/#([1-9]\d*)/g)) {
          const number = Number(reference[1]);
          if (!Number.isSafeInteger(number)) {
            errors.push('Child-plan issue numbers must be positive safe integers.');
          } else if (declaredChildren.includes(number)) {
            errors.push(`The child-plan declaration repeats #${number}.`);
          } else {
            declaredChildren.push(number);
          }
        }
      }
    }
  }

  return {
    flatGraph: validFlatDeclarations.length === 1 &&
      flatDeclarations.length === 1,
    firstWave,
    childPlanState,
    declaredChildren,
    errors,
  };
}

export function normalizeEdges(edges = [], childNumbers = new Set()) {
  const unique = new Map();
  for (const edge of edges) {
    const blocker = issueNumber(edge?.blocker);
    const blocked = issueNumber(edge?.blocked);
    if (
      blocker === undefined ||
      blocked === undefined ||
      blocker === blocked ||
      !childNumbers.has(blocker) ||
      !childNumbers.has(blocked)
    ) {
      continue;
    }
    unique.set(`${blocker}->${blocked}`, { blocker, blocked });
  }
  return [...unique.values()].sort(
    (left, right) => left.blocker - right.blocker ||
      left.blocked - right.blocked,
  );
}

export function evaluateEpicDependencies({ issue, children = [], edges = [] }) {
  if (!hasEpicLabel(issue?.labels)) {
    return {
      classification: 'NOT_APPLICABLE',
      passed: false,
      reason: `#${issue?.number ?? '?'} is not labelled ${epicLabel}.`,
      childCount: 0,
      edgeCount: 0,
      flatGraph: false,
      firstWave: [],
      isolatedChildren: [],
      errors: [],
    };
  }

  const childNumbers = new Set();
  const errors = [];
  for (const child of children) {
    const number = issueNumber(child?.number);
    if (number === undefined) {
      errors.push('A sub-issue is missing a valid positive issue number.');
    } else if (childNumbers.has(number)) {
      errors.push(`Sub-issue #${number} appears more than once.`);
    } else {
      childNumbers.add(number);
    }
  }

  const declarations = parseEpicDeclarations(issue?.body ?? '');
  errors.push(...declarations.errors);
  const missingChildren = declarations.declaredChildren
    .filter((number) => !childNumbers.has(number));
  const childPlan = {
    childPlanState: declarations.childPlanState,
    declaredChildren: declarations.declaredChildren,
    missingChildren,
  };
  if (missingChildren.length > 0) {
    errors.push('Finalized child-plan issues are not linked sub-issues: ' +
      missingChildren.map((number) => `#${number}`).join(', '));
  }
  if (declarations.childPlanState === 'empty' && childNumbers.size > 0) {
    errors.push('The child plan declares an empty epic but linked sub-issues exist.');
  }
  const unknownFirstWave = declarations.firstWave
    .filter((number) => !childNumbers.has(number));
  if (unknownFirstWave.length > 0) {
    errors.push(
      `First-wave issues are not linked sub-issues: ` +
      unknownFirstWave.map((number) => `#${number}`).join(', '),
    );
  }

  const normalizedEdges = normalizeEdges(edges, childNumbers);
  const connected = new Set();
  for (const edge of normalizedEdges) {
    connected.add(edge.blocker);
    connected.add(edge.blocked);
  }
  const firstWave = new Set(declarations.firstWave);
  const isolatedChildren = [...childNumbers]
    .filter((number) => !connected.has(number) && !firstWave.has(number))
    .sort((left, right) => left - right);

  if (childNumbers.size === 0 && errors.length === 0) {
    return {
      classification: 'PASS',
      passed: true,
      reason: declarations.childPlanState === 'draft'
        ? 'Draft child plan: no linked sub-issues yet; plan completeness is not verified.'
        : 'Explicitly empty child plan has no linked sub-issues.',
      ...childPlan,
      childCount: 0,
      edgeCount: 0,
      flatGraph: declarations.flatGraph,
      firstWave: declarations.firstWave,
      isolatedChildren: [],
      errors: [],
    };
  }

  if (declarations.flatGraph && normalizedEdges.length > 0) {
    errors.push(
      'The epic declares a flat graph but has machine-readable dependency edges.',
    );
  }
  if (
    !declarations.flatGraph &&
    childNumbers.size > 0 &&
    normalizedEdges.length === 0
  ) {
    errors.push(
      `Epic has ${childNumbers.size} linked sub-issues but zero internal ` +
      `dependency edges. Add edges or declare ${flatGraphMarker}.`,
    );
  }
  if (!declarations.flatGraph && isolatedChildren.length > 0) {
    errors.push(
      `Linked sub-issues have neither blockers nor dependents and are not in ` +
      `the declared first wave: ` +
      isolatedChildren.map((number) => `#${number}`).join(', '),
    );
  }

  if (errors.length > 0) {
    return {
      classification: 'FAIL',
      passed: false,
      reason: errors.join(' '),
      ...childPlan,
      childCount: childNumbers.size,
      edgeCount: normalizedEdges.length,
      flatGraph: declarations.flatGraph,
      firstWave: declarations.firstWave,
      isolatedChildren,
      errors,
    };
  }

  const graphReason = declarations.flatGraph
    ? `Flat graph explicitly declared for ${childNumbers.size} linked sub-issues.`
    : `Verified ${normalizedEdges.length} dependency edges across ` +
      `${childNumbers.size} linked sub-issues.`;
  return {
    classification: 'PASS',
    passed: true,
    reason: declarations.childPlanState === 'draft'
      ? `Draft child plan: plan completeness is not verified. ${graphReason}`
      : `All ${declarations.declaredChildren.length} declared children are linked. ${graphReason}`,
    ...childPlan,
    childCount: childNumbers.size,
    edgeCount: normalizedEdges.length,
    flatGraph: declarations.flatGraph,
    firstWave: declarations.firstWave,
    isolatedChildren: [],
    errors: [],
  };
}

export function exitCodeFor(classification) {
  switch (classification) {
    case 'PASS':
      return 0;
    case 'FAIL':
      return 2;
    case 'NOT_APPLICABLE':
      return 4;
    default:
      return 1;
  }
}

export function formatGateComment(result, issueNumberValue, runUrl) {
  const status = result.classification === 'PASS' ? 'PASS'
    : result.classification === 'FAIL' ? 'FAIL'
      : 'NOT APPLICABLE';
  const firstWave = result.firstWave.length > 0
    ? result.firstWave.map((number) => `#${number}`).join(', ')
    : '(none)';
  const isolated = result.isolatedChildren.length > 0
    ? result.isolatedChildren.map((number) => `#${number}`).join(', ')
    : '(none)';
  const declaredChildren = (result.declaredChildren ?? [])
    .map((number) => `#${number}`).join(', ') || '(none)';
  const missingChildren = (result.missingChildren ?? [])
    .map((number) => `#${number}`).join(', ') || '(none)';
  return [
    gateCommentMarker,
    `## Epic dependency gate: ${status}`,
    '',
    `**Issue:** #${issueNumberValue}`,
    '',
    `**Reason:** ${result.reason}`,
    '',
    '| Field | Value |',
    '|---|---|',
    `| Child-plan state | ${result.childPlanState ?? 'unspecified'} |`,
    `| Declared children | ${declaredChildren} |`,
    `| Missing native child links | ${missingChildren} |`,
    `| Linked sub-issues | ${result.childCount} |`,
    `| Internal dependency edges | ${result.edgeCount} |`,
    `| Flat-graph opt-out | ${result.flatGraph ? 'yes' : 'no'} |`,
    `| Declared first wave | ${firstWave} |`,
    `| Undeclared isolated children | ${isolated} |`,
    '',
    `[Workflow run](${runUrl})`,
  ].join('\n');
}

function parseArgs(argv) {
  const args = {};
  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (argument === '--repo' || argument === '--issue') {
      args[argument.slice(2)] = argv[index + 1];
      index += 1;
      continue;
    }
    if (argument === '--json') {
      args.json = true;
      continue;
    }
    throw new Error(`Unknown argument: ${argument}`);
  }
  if (!/^[A-Za-z0-9._-]+\/[A-Za-z0-9._-]+$/.test(args.repo ?? '')) {
    throw new Error('--repo must be OWNER/REPOSITORY.');
  }
  return {
    ...args,
    issue: parsePositiveSafeInteger(args.issue, '--issue'),
  };
}

function ghApi(path) {
  const output = execFileSync('gh', ['api', path], {
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'inherit'],
  });
  return JSON.parse(output);
}

function ghApiPaginated(path) {
  const output = execFileSync(
    'gh',
    ['api', '--paginate', '--slurp', path],
    {
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'inherit'],
    },
  );
  const pages = JSON.parse(output);
  return pages.flatMap((page) => Array.isArray(page) ? page : [page]);
}

function loadEpicGraph(repository, epicNumber) {
  const issue = ghApi(`/repos/${repository}/issues/${epicNumber}`);
  const children = ghApiPaginated(
    `/repos/${repository}/issues/${epicNumber}/sub_issues?per_page=100`,
  );
  const edges = [];
  for (const child of children) {
    const blockers = ghApiPaginated(
      `/repos/${repository}/issues/${child.number}` +
      '/dependencies/blocked_by?per_page=100',
    );
    for (const blocker of blockers) {
      edges.push({ blocker: blocker.number, blocked: child.number });
    }
    const dependents = ghApiPaginated(
      `/repos/${repository}/issues/${child.number}` +
      '/dependencies/blocking?per_page=100',
    );
    for (const dependent of dependents) {
      edges.push({ blocker: child.number, blocked: dependent.number });
    }
  }
  return { issue, children, edges };
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const result = evaluateEpicDependencies(loadEpicGraph(args.repo, args.issue));
  if (args.json) {
    process.stdout.write(`${JSON.stringify(result)}\n`);
  } else {
    process.stdout.write(`${result.classification}: ${result.reason}\n`);
  }
  process.exitCode = exitCodeFor(result.classification);
}

const invokedPath = process.argv[1]
  ? pathToFileURL(process.argv[1]).href
  : undefined;
if (import.meta.url === invokedPath) {
  main().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
