import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { editedFiles, formatContext, runHook } from '../post-edit-hook.mjs';
import { readSharedConfig } from '../check-ui-design.mjs';

const testsDirectory = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(testsDirectory, '..', '..', '..');

test('parses apply_patch strings with moves, deletions, spaces, and mixed files', () => {
  const patch = [
    '*** Begin Patch',
    '*** Update File: src/Web/ReactApp/src/Old view.tsx',
    '*** Move to: src/Web/ReactApp/src/New view.tsx',
    '@@',
    '*** Delete File: src/Web/ReactApp/src/Deleted view.tsx',
    '*** Add File: notes/non-ui file.txt',
    '*** End Patch',
  ].join('\n');

  assert.deepEqual(editedFiles({ toolName: 'apply_patch', toolArgs: patch }), [
    { path: 'src/Web/ReactApp/src/New view.tsx', operation: 'move' },
    { path: 'src/Web/ReactApp/src/Deleted view.tsx', operation: 'delete' },
    { path: 'notes/non-ui file.txt', operation: 'add' },
  ]);
});

test('parses nested input, patch variants, and ordinary edit path objects', () => {
  const patch = [
    '*** Begin Patch',
    '*** Add File: src/Web/ReactApp/src/Added with spaces.tsx',
    '*** End Patch',
  ].join('\n');

  assert.deepEqual(
    editedFiles({ toolName: 'apply_patch', toolArgs: { input: patch } }),
    [{ path: 'src/Web/ReactApp/src/Added with spaces.tsx', operation: 'add' }],
  );
  assert.deepEqual(
    editedFiles({ toolName: 'apply_patch', tool_input: { patch } }),
    [{ path: 'src/Web/ReactApp/src/Added with spaces.tsx', operation: 'add' }],
  );
  assert.deepEqual(
    editedFiles({
      toolName: 'edit_file',
      toolArgs: JSON.stringify({ input: { file_path: 'src/Web/ReactApp/src/Edited.tsx' } }),
    }),
    [{ path: 'src/Web/ReactApp/src/Edited.tsx', operation: 'update' }],
  );
  assert.deepEqual(
    editedFiles({ toolName: 'delete_file', toolArgs: { input: { path: 'src/Web/ReactApp/src/Gone.tsx' } } }),
    [{ path: 'src/Web/ReactApp/src/Gone.tsx', operation: 'delete' }],
  );
});

test('does not read deleted paths from an apply_patch event', async () => {
  const result = await runHook({
    cwd: root,
    toolName: 'apply_patch',
    toolArgs: '*** Begin Patch\n*** Delete File: src/Web/ReactApp/src/missing-deleted-file.tsx\n*** End Patch',
  });
  assert.equal(result, undefined);
});

test('preserves UI findings when an apply_patch event also edits non-UI files', async () => {
  const result = await runHook({
    cwd: root,
    toolName: 'apply_patch',
    toolArgs: [
      '*** Begin Patch',
      '*** Update File: src/Web/ReactApp/src/ui.tsx',
      '*** Add File: notes/ordinary.txt',
      '*** End Patch',
    ].join('\n'),
  }, {
    scanFile: async (_root, filePath) => filePath.endsWith('ordinary.txt')
      ? { status: 'skipped' }
      : {
        status: 'fail',
        scannedFiles: ['src/Web/ReactApp/src/ui.tsx'],
        findings: [{
          file: 'src/Web/ReactApp/src/ui.tsx',
          line: 7,
          antipattern: 'small-target',
          description: 'Interactive target is too small.',
        }],
        advisories: [],
      },
  });

  assert.match(result.additionalContext, /src\/Web\/ReactApp\/src\/ui\.tsx:7 \[small-target\]/);
  assert.doesNotMatch(result.additionalContext, /could not run/i);
  assert.doesNotMatch(result.additionalContext, /undefined/);
});

test('runs with complete defaults when shared config is absent', async () => {
  const missingConfigRoot = path.join(testsDirectory, 'fixtures', 'no-shared-config');
  const config = readSharedConfig(missingConfigRoot);
  assert.deepEqual(config.hook, {
    enabled: true,
    quiet: false,
    limits: { maxFindings: 5, maxChars: 8000 },
  });

  const result = await runHook({
    cwd: root,
    toolName: 'edit_file',
    toolArgs: { path: 'src/Web/ReactApp/src/ui.tsx' },
  }, {
    configReader: () => readSharedConfig(missingConfigRoot),
    scanFile: async () => ({
      status: 'fail',
      scannedFiles: ['src/Web/ReactApp/src/ui.tsx'],
      findings: [{
        file: 'src/Web/ReactApp/src/ui.tsx',
        line: 3,
        antipattern: 'small-target',
        description: 'Interactive target is too small.',
      }],
      advisories: [],
    }),
  });

  assert.match(result.additionalContext, /src\/Web\/ReactApp\/src\/ui\.tsx:3 \[small-target\]/);
  assert.doesNotMatch(result.additionalContext, /could not run/i);
});

test('caps aggregate multi-file findings and reports omissions', () => {
  const results = [
    {
      scannedFiles: ['one.tsx'],
      findings: [{ file: 'one.tsx', line: 2, antipattern: 'rule-one', description: 'First issue.' }],
      advisories: [],
    },
    {
      scannedFiles: ['two.tsx'],
      findings: [{ file: 'two.tsx', line: 4, antipattern: 'rule-two', description: 'Second issue.' }],
      advisories: [],
    },
  ];
  const byCount = formatContext(results, { maxFindings: 1, maxChars: 1000 });
  assert.match(byCount, /one\.tsx:2/);
  assert.doesNotMatch(byCount, /two\.tsx:4/);
  assert.match(byCount, /1 finding\(s\) omitted/);

  const oversized = [
    {
      scannedFiles: ['one.tsx', 'two.tsx'],
      findings: [
        { file: 'one.tsx', line: 2, antipattern: 'rule-one', description: 'x'.repeat(500) },
        { file: 'two.tsx', line: 4, antipattern: 'rule-two', description: 'y'.repeat(500) },
      ],
      advisories: [],
    },
  ];
  const byCharacters = formatContext(oversized, { maxFindings: 5, maxChars: 160 });
  assert.ok(byCharacters.length <= 160);
  assert.match(byCharacters, /2 finding\(s\) omitted/);
});

test('configures the same Node hook command for bash and PowerShell', () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(root, '.github', 'hooks', 'impeccable.json'), 'utf8'));
  const hook = manifest.hooks.postToolUse[0];
  assert.equal(hook.bash, hook.powershell);
  for (const shell of ['bash', 'powershell']) {
    assert.match(hook[shell], /^node /);
    assert.match(hook[shell], /scripts\/design\/post-edit-hook\.mjs/);
  }
});
