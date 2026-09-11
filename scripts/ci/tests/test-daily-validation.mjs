import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { category } from '../daily-validation-reporter.mjs';

test('reporter distinguishes explicit skips, serial omissions, interruptions and flaky passes', () => {
  const item = { annotations: [], outcome: () => 'expected' };
  assert.equal(category(item, []), 'did-not-run');
  assert.equal(category(item, [{ status: 'skipped' }]), 'did-not-run');
  assert.equal(category(item, [{ status: 'interrupted' }]), 'did-not-run');
  assert.equal(category({ ...item, annotations: [{ type: 'skip' }] }, [{ status: 'skipped' }]), 'skipped');
  assert.equal(category({ ...item, outcome: () => 'unexpected' }, [{ status: 'failed' }]), 'failed');
  assert.equal(category({ ...item, outcome: () => 'flaky' }, [{ status: 'failed' }, { status: 'passed' }]), 'passed');
});

test('Python runner regressions', () => {
  const result = spawnSync(process.platform === 'win32' ? 'python' : 'python3',
    ['scripts/ci/tests/test_daily_validation.py'], { encoding: 'utf8' });
  assert.equal(result.status, 0, result.stdout + result.stderr);
});

test('prompt preserves unresolved assertion classification without inventing a verdict', () => {
  const prompt = readFileSync('docs/DAILY_UI_VALIDATION_PROMPT.md', 'utf8');
  assert.match(prompt, /unresolved classification/);
  assert.match(prompt, /link an investigation issue, and report INCOMPLETE classification/);
  assert.match(prompt, /An executed\s+assertion failure is not automatically a product defect or an infrastructure blocker/);
  assert.match(prompt, /VALIDATION INCOMPLETE:[\s\S]*executed failure still has unresolved classification/);
});

test('external hosting preserves tested Playwright config and exact selectors', () => {
  const runner = readFileSync('scripts/ci/daily-validation.py', 'utf8');
  assert.match(runner, /import original from/);
  assert.match(runner, /\.\.\.original, webServer: undefined/);
  assert.match(runner, /reporter: \[\.\.\.original.reporter/);
  assert.match(runner, /test:e2e:moonraker", "--", "--project=chromium"/);
  assert.match(runner, /"--workers=1", "--grep-invert", "Moonraker"/);
  assert.match(runner, /"--no-build", "--pull", "never"/);
  assert.doesNotMatch(runner, /"--with-deps"|npm run dev|npm run build|docker system prune/);
});
