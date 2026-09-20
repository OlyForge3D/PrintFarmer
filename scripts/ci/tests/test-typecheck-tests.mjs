import assert from 'node:assert/strict';
import test from 'node:test';
import { evaluate } from '../../../src/Web/ReactApp/scripts/typecheck-tests.mjs';

const directory = 'D:/repo/src/Web/ReactApp';
const baseline = { testDiagnosticCount: 1, minimumTestFileCount: 1 };
const testDiagnostic = 'src/test/example.test.ts(1,1): error TS2322: Type error.';
const appDiagnostic = 'src/services/example.ts(1,1): error TS2322: Type error.';
const listFiles = 'src/test/example.test.ts';

function evaluateGate(overrides = {}) {
  return evaluate({
    baseline,
    compilerResult: { status: 2, signal: null, error: undefined },
    listFilesResult: { status: 0, signal: null, error: undefined },
    output: testDiagnostic,
    listFilesOutput: listFiles,
    directory,
    ...overrides,
  });
}

test('accepts an exact test diagnostic baseline', () => {
  assert.equal(evaluateGate().ok, true);
});

test('fails compiler signal death and null status', () => {
  assert.equal(evaluateGate({ compilerResult: { status: null, signal: 'SIGKILL', error: undefined } }).ok, false);
  assert.equal(evaluateGate({ compilerResult: { status: null, signal: null, error: undefined } }).ok, false);
});

test('fails global, non-file compiler diagnostics', () => {
  const result = evaluateGate({ output: 'error TS18003: No inputs were found in config file.' });
  assert.equal(result.ok, false);
  assert.match(result.message, /global diagnostic/);
});

test('fails nonzero compiler exits without file diagnostics', () => {
  const result = evaluateGate({ output: '', compilerResult: { status: 2, signal: null, error: undefined } });
  assert.equal(result.ok, false);
  assert.match(result.message, /without file diagnostics/);
});

test('fails an unsuccessful project-file listing', () => {
  const result = evaluateGate({ listFilesResult: { status: 2, signal: null, error: undefined } });
  assert.equal(result.ok, false);
  assert.match(result.message, /could not list/);
});

test('fails below the exact test-file floor', () => {
  const result = evaluateGate({ baseline: { ...baseline, minimumTestFileCount: 2 } });
  assert.equal(result.ok, false);
  assert.match(result.message, /below the 2-file floor/);
});

test('fails above and below the exact diagnostic baseline', () => {
  const above = evaluateGate({ output: `${testDiagnostic}\n${testDiagnostic.replace('(1,1)', '(2,1)')}` });
  const below = evaluateGate({ output: appDiagnostic });
  assert.equal(above.ok, false);
  assert.match(above.message, /do not raise the baseline/);
  assert.equal(below.ok, false);
  assert.match(below.message, /baseline is stale/);
});

test('fails missing or malformed baseline keys', () => {
  assert.equal(evaluateGate({ baseline: { minimumTestFileCount: 1 } }).ok, false);
  assert.equal(evaluateGate({ baseline: { testDiagnosticCount: 1 } }).ok, false);
  assert.equal(evaluateGate({ baseline: { testDiagnosticCount: 1.5, minimumTestFileCount: 1 } }).ok, false);
});
