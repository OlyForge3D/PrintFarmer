import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import yaml from 'js-yaml';

const repositoryRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  '..', '..', '..',
);

function parseFrontmatter(markdown) {
  const match = markdown.match(/^---\r?\n([\s\S]*?)\r?\n---\r?\n([\s\S]*)$/);
  if (!match) {
    throw new Error('Markdown file lacks valid YAML frontmatter');
  }
  return {
    meta: yaml.load(match[1]),
    body: match[2],
  };
}

test('reviewer agent definitions: tool grants, model diversity, and read-only boundaries', async (t) => {
  const agentSpecs = [
    {
      file: '.github/agents/code-review-opus.agent.md',
      expectedName: 'Bishop',
      expectedModelFamily: 'opus',
      expectedReviewer: 'bishop',
      expectedLens: 'integration/architecture',
    },
    {
      file: '.github/agents/code-review-gemini.agent.md',
      expectedName: 'Vasquez',
      expectedModelFamily: 'gemini',
      expectedReviewer: 'vasquez',
      expectedLens: 'trust/failure/concurrency',
    },
    {
      file: '.github/agents/code-review-codex.agent.md',
      expectedName: 'Hicks',
      expectedModelFamily: 'gpt-5.6',
      expectedReviewer: 'hicks',
      expectedLens: 'behavior/contracts/tests',
    },
  ];

  const modelsSeen = new Set();

  for (const spec of agentSpecs) {
    await t.test(`validates agent definition ${spec.file}`, async () => {
      const filePath = path.join(repositoryRoot, spec.file);
      const content = await readFile(filePath, 'utf8');
      const { meta, body } = parseFrontmatter(content);

      // Verify frontmatter structure
      assert.equal(meta.name, spec.expectedName, `Agent name should be ${spec.expectedName}`);
      assert.ok(meta.description && meta.description.length > 0, 'Agent must have a description');
      assert.ok(meta.model, 'Agent must specify a model');
      assert.ok(
        meta.model.toLowerCase().includes(spec.expectedModelFamily),
        `Model ${meta.model} should belong to family ${spec.expectedModelFamily}`,
      );
      modelsSeen.add(meta.model);

      // Verify tool grants: MUST be ["*"] to ensure CLI task agents have access to CLI tools
      // rather than non-existent IDE tools (e.g. read/problems)
      assert.deepEqual(
        meta.tools,
        ['*'],
        `Agent ${spec.file} must have tools: ["*"] for CLI tool compatibility`,
      );

      // Verify read-only constraints in agent instructions
      assert.ok(
        body.includes('READ-ONLY') || body.includes('read-only'),
        'Agent prompt must enforce read-only operation',
      );
      assert.ok(
        body.includes('Copilot-Processing.md'),
        'Agent prompt must forbid creating/editing Copilot-Processing.md',
      );
      assert.ok(
        body.includes('dotnet build') || body.includes('npm install') || body.includes('builds, installs, or tests'),
        'Agent prompt must forbid running builds, installs, or test suites',
      );

      // Verify canonical squad verdict format
      assert.ok(
        body.includes('<!-- squad-verdict -->'),
        'Agent prompt must include canonical <!-- squad-verdict --> marker',
      );
      assert.ok(
        body.includes('Squad-Reviewer:'),
        'Agent prompt must include Squad-Reviewer field in template',
      );
      assert.ok(
        body.includes(`Squad-Reviewer: ${spec.expectedReviewer}`),
        `Agent prompt must include Squad-Reviewer: ${spec.expectedReviewer}`,
      );
      assert.ok(
        body.includes('Squad-Verdict:'),
        'Agent prompt must include Squad-Verdict field in template',
      );
      assert.ok(
        body.includes('Squad-Head-SHA:'),
        'Agent prompt must include Squad-Head-SHA field in template',
      );
      for (const text of [
        spec.expectedLens, 'stable finding ID', '**Failure scenario:**', '**Owner:**',
        '**Closure criteria:**', '## Checkpoint', 'immutable', 'continuation-first',
        'complete per-reviewer prior-SHA -> new-head delta', 'fresh current-head evidence',
      ]) {
        assert.ok(body.includes(text), `${spec.file} must include ${text}`);
      }
    });
  }

  await t.test('review panel model diversity: all 3 reviewers use distinct model families', () => {
    assert.equal(modelsSeen.size, 3, 'All 3 review panel agents must use distinct models');
  });
});

test('review prompt template (.github/prompts/review.prompt.md)', async () => {
  const filePath = path.join(repositoryRoot, '.github/prompts/review.prompt.md');
  const content = await readFile(filePath, 'utf8');
  const { meta, body } = parseFrontmatter(content);

  assert.equal(meta.name, 'review');
  assert.ok(body.includes('Bishop') && body.includes('Hicks') && body.includes('Vasquez'));
  assert.ok(body.includes('read-only'));
  assert.ok(body.includes('<!-- squad-verdict -->'));
  assert.ok(body.includes('agent_type: "code-review"'));
  assert.ok(!/agent_type:\s*"Code Review \(/i.test(body), 'Prompt must not reference legacy Code Review (...) agent types');
  assert.match(body, /High-risk work gets two differentiated reviewers/);
  assert.match(body, /one qualified non-author reviewer from a different model family/);
  assert.match(body, /third only for disagreement,\s+a critical finding, or unresolved cross-domain risk/);
  assert.match(body, /continuation-first/);
  assert.doesNotMatch(body, /3\/3|unanimous APPROVE|MUST inline the full persona/);
});

test('lean policy is canonical and all readiness entry points link to it', async () => {
  const canonicalPath = path.join(repositoryRoot, '.github/copilot-instructions.md');
  const canonical = await readFile(canonicalPath, 'utf8');
  for (const invariant of [
    /Draft PRs may open early; review gates readiness and merge/,
    /High-risk changes require TWO\s+qualified reviewers/,
    /accepts any two eligible panel approvals/,
    /no accepted current-head rejection/,
    /third reviewer only for disagreement, a critical finding, or unresolved\s+cross-domain risk/,
    /one deduplicated finding ledger/,
    /stable ID/,
    /severity/,
    /failure scenario, fix owner,\s+closure criteria/,
    /compact immutable per-reviewer checkpoint/,
    /Never edit a prior checkpoint/,
    /copies all unresolved findings into every follow-on packet/,
    /owner override marks dissent overridden, never verified or withdrawn/,
    /dissent=N; short=N/,
    /--match-head-commit/,
  ]) {
    assert.match(canonical, invariant);
  }
  for (const file of [
    '.github/prompts/review.prompt.md', '.github/agents/squad.agent.md',
    '.github/ralph-reference.md', '.github/pull_request_template.md',
    '.github/skills/reviewer-protocol/SKILL.md',
    '.squad/issue-lifecycle.md', '.squad/templates/issue-lifecycle.md',
    ...['bishop', 'hicks', 'vasquez'].map((member) => `.squad/agents/${member}/charter.md`),
  ]) {
    const content = await readFile(path.join(repositoryRoot, file), 'utf8');
    const link = content.match(/\[[^\]]+\]\(([^)]+)#risk-based-review-scope\)/);
    assert.ok(link, `${file} must link reviewer routing to canonical policy`);
    assert.equal(path.resolve(repositoryRoot, path.dirname(file), link[1]), canonicalPath);
    assert.doesNotMatch(content,
      /Pre-PR Review Gate|pre-PR review gate|3\/3 APPROVE|before any PR is opened/,
      `${file} must not restore pre-creation or routine three-reviewer gates`);
  }
});

test('lifecycle copies gate readiness and use exact-head merge, not CI-only merge', async () => {
  for (const file of ['.squad/issue-lifecycle.md', '.squad/templates/issue-lifecycle.md']) {
    const content = await readFile(path.join(repositoryRoot, file), 'utf8');
    assert.match(content, /Draft PRs may open before\s+review/);
    assert.match(content, /Mark ready only after required review, CI, and current-head evidence pass/);
    assert.match(content, /gh pr create --draft/);
    assert.match(content, /--label squad/);
    assert.match(content, /Closes #/);
    for (const command of content.matchAll(/^gh pr merge .*$/gm)) {
      assert.match(command[0], /--match-head-commit/);
    }
    assert.doesNotMatch(content, /CI-only projects|If CI passes, Ralph auto-merges|Single Agent, No Review/);
  }
});

test('lockout examples require explicit invocation rather than ordinary rejection', async () => {
  const content = await readFile(
    path.join(repositoryRoot, '.github/skills/reviewer-protocol/SKILL.md'), 'utf8',
  );
  for (const name of ['Example 3:', 'Example 4:']) {
    const example = content.split(name)[1].split('**Example')[0];
    assert.match(example, /rejected with explicit lockout/);
  }
  assert.match(content, /Example 5: Ordinary rejection, no lockout/);
});

test('panel rereview entry points link to the canonical delta-only scope', async () => {
  const entryPoints = [
    '.github/agents/code-review-opus.agent.md',
    '.github/agents/code-review-codex.agent.md',
    '.github/agents/code-review-gemini.agent.md',
    '.github/agents/squad.agent.md',
    '.github/prompts/review.prompt.md',
    '.github/skills/reviewer-protocol/SKILL.md',
    '.github/ralph-reference.md',
    ...['bishop', 'hicks', 'vasquez'].map((member) => `.squad/agents/${member}/charter.md`),
  ];
  const canonicalPath = path.join(repositoryRoot, '.github/copilot-instructions.md');
  const canonical = await readFile(canonicalPath, 'utf8');
  const section = canonical.split('### Delta-Only Panel Rereview\n')[1]?.split('\n### ')[0];
  assert.ok(section, 'A single canonical rereview scope must exist');
  assert.equal(canonical.match(/^### Delta-Only Panel Rereview$/gm)?.length, 1);
  for (const invariant of [
    /only the revision delta/,
    /git diff <last-reviewed-sha> <new-head-sha>/,
    /not\s+a merge-base\/three-dot diff/,
    /surrounding code, callers, and tests/,
    /every.*\n.*change in that range/,
    /unresolved prior findings/,
    /their own last reviewed SHA/,
    /recover it before proceeding/,
    /Squad-Head-SHA.*equal to the\s+\*\*new current head\*\*/,
    /full PR change/,
    /never from the smaller delta/,
  ]) {
    assert.match(section, invariant);
  }
  for (const file of entryPoints) {
    const content = await readFile(path.join(repositoryRoot, file), 'utf8');
    const link = content.match(/\[Delta-Only Panel Rereview\]\(([^)]+)#delta-only-panel-rereview\)/);
    assert.ok(link, `${file} must route follow-on rounds to canonical policy`);
    assert.equal(path.resolve(repositoryRoot, path.dirname(file), link[1]), canonicalPath);
  }
});
