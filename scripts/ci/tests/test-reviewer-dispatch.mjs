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
    },
    {
      file: '.github/agents/code-review-gemini.agent.md',
      expectedName: 'Vasquez',
      expectedModelFamily: 'gemini',
      expectedReviewer: 'vasquez',
    },
    {
      file: '.github/agents/code-review-codex.agent.md',
      expectedName: 'Hicks',
      expectedModelFamily: 'gpt-5.6',
      expectedReviewer: 'hicks',
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
});


