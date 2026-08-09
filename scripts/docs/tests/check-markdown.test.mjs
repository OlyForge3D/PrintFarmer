import assert from 'node:assert/strict';
import {
  mkdir,
  mkdtemp,
  rm,
  writeFile,
} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { checkMarkdown, githubSlug } from '../check-markdown.mjs';

async function withFixture(files, verify) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'printfarmer-doc-health-'));
  try {
    for (const [relativePath, content] of Object.entries(files)) {
      const filePath = path.join(root, relativePath);
      await mkdir(path.dirname(filePath), { recursive: true });
      await writeFile(filePath, content, 'utf8');
    }
    await verify(await checkMarkdown(root));
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

test('accepts files, fragments, encoded paths, and directory index targets', async () => {
  await withFixture({
    'README.md': [
      '[guide](docs/Guide.md#repeated-heading-1)',
      '[inline code heading](docs/Guide.md#worker-auth-setup)',
      '[encoded](docs/Encoded%20Name.md)',
      '[directory](docs/reference/#directory-index)',
    ].join('\n'),
    'docs/Guide.md': [
      '# Repeated heading',
      '',
      '## Repeated heading',
      '',
      '## `Worker Auth` setup',
    ].join('\n'),
    'docs/Encoded Name.md': '# Encoded\n',
    'docs/reference/INDEX.md': '# Directory index\n',
  }, (result) => {
    assert.equal(result.inventory.length, 4);
    assert.deepEqual(result.issues, []);
  });
});

test('reports missing files, bad fragments, and case mismatches', async () => {
  await withFixture({
    'README.md': [
      '[missing](docs/Missing.md)',
      '[fragment](docs/Guide.md#missing-heading)',
      '[case](docs/guide.md)',
    ].join('\n'),
    'docs/Guide.md': '# Existing heading\n',
  }, (result) => {
    assert.deepEqual(
      result.issues.map((issue) => issue.type),
      ['invalid-target', 'missing-fragment', 'invalid-target'],
    );
    assert.match(result.issues[2].detail, /case mismatch/);
  });
});

test('excludes external URLs and intentionally non-file URI schemes', async () => {
  await withFixture({
    'README.md': [
      '[https](https://example.com/docs)',
      '[protocol relative](//example.com/docs)',
      '[mail](mailto:docs@example.com)',
      '[data](data:text/plain,hello)',
      '[local](docs/Guide.md)',
    ].join('\n'),
    'docs/Guide.md': '# Guide\n',
  }, (result) => {
    assert.deepEqual(
      result.inventory.map((entry) => entry.destination),
      ['docs/Guide.md'],
    );
    assert.deepEqual(result.issues, []);
  });
});

test('reports malformed document structure', async () => {
  await withFixture({
    'README.md': '# Healthy\n',
    'docs/Corrupted.md': [
      '# Corrupted',
      '',
      '```csharp',
      'public sealed class Example',
      '# Implementation Status',
      '{',
      '```',
      '',
      '~~~typescript',
      'const unfinished = true;',
    ].join('\n'),
  }, (result) => {
    assert.deepEqual(
      result.issues.map((issue) => issue.type),
      ['markdown-heading-in-code-fence', 'unclosed-code-fence'],
    );
  });
});

test('accepts skipped schemes in reference definitions and validates local definitions', async () => {
  await withFixture({
    'README.md': [
      '[local][guide]',
      '[remote][website]',
      '',
      '[guide]: <docs/Guide.md#guide>',
      '[website]: ssh://example.com/docs',
    ].join('\n'),
    'docs/Guide.md': '# Guide\n',
  }, (result) => {
    assert.deepEqual(
      result.inventory.map((entry) => entry.destination),
      ['docs/Guide.md#guide'],
    );
    assert.deepEqual(result.issues, []);
  });
});

test('githubSlug never leaves tag-delimiter characters in its output, even for nested/malformed markup', () => {
  const nestedHeadings = [
    'Alpha <a<b<c>d>e> Beta',
    '<outer<inner>tail>',
    'Mixed <<b>> markup',
  ];
  for (const heading of nestedHeadings) {
    const slug = githubSlug(heading);
    assert.doesNotMatch(slug, /[<>]/, `slug for ${JSON.stringify(heading)} kept a tag delimiter: ${slug}`);
  }
});

test('githubSlug decodes each XML entity exactly once, without double-unescaping', () => {
  // A heading containing the literal text "&lt;" encoded as "&amp;lt;" must
  // decode to the literal text "&lt;", not further to "<". Decoding entities
  // sequentially instead of in one pass would double-unescape this value.
  assert.equal(githubSlug('&amp;lt;value&amp;gt;'), 'ltvaluegt');
});
