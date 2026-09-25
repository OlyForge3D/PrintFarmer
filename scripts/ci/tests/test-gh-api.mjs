import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import test from 'node:test';

import { ghApiMaxBuffer, readGhJson } from '../gh-api.mjs';

// Runs a real Node child in place of `gh` with the exact options readGhJson
// passes, so the size boundary exercised here is Node's own maxBuffer
// enforcement rather than a mocked approximation of it.
function childEmitting(byteLength) {
  const calls = [];
  const exec = (command, args, options) => {
    calls.push({ command, args, options });
    const script =
      `const n = ${byteLength};` +
      'process.stdout.write(JSON.stringify("x".repeat(n - 2)));';
    return execFileSync(process.execPath, ['-e', script], options);
  };
  return { exec, calls };
}

const limit = 4096;

test('parses a response below the transport limit', () => {
  const { exec } = childEmitting(limit - 1);
  assert.equal(readGhJson(['api', 'x'], { exec, maxBuffer: limit }).length, limit - 3);
});

test('parses a response exactly at the transport limit', () => {
  const { exec } = childEmitting(limit);
  assert.equal(readGhJson(['api', 'x'], { exec, maxBuffer: limit }).length, limit - 2);
});

test('fails closed on a response one byte above the transport limit', () => {
  const { exec } = childEmitting(limit + 1);
  assert.throws(
    () => readGhJson(['api', 'repos/o/r/compare/a...b'], { exec, maxBuffer: limit }),
    (error) =>
      /exceeded the 4096-byte transport limit/.test(error.message) &&
      error.cause?.code === 'ENOBUFS',
  );
});

test('the default limit is explicit, bounded, and above Node\'s 1 MiB default (#2988)', () => {
  assert.equal(ghApiMaxBuffer, 32 * 1024 * 1024);
  const calls = [];
  readGhJson(['api', 'x'], {
    exec: (command, args, options) => {
      calls.push({ command, args, options });
      return '{}';
    },
  });
  assert.deepEqual(calls, [{
    command: 'gh',
    args: ['api', 'x'],
    options: {
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'inherit'],
      maxBuffer: ghApiMaxBuffer,
    },
  }]);
});

test('a response above Node\'s old 1 MiB default now parses', () => {
  const { exec } = childEmitting(2 * 1024 * 1024);
  assert.equal(typeof readGhJson(['api', 'x'], { exec }), 'string');
});

test('the transport limit can be shortened but never enlarged or disabled', () => {
  const exec = () => '{}';
  for (const maxBuffer of [
    ghApiMaxBuffer + 1, Infinity, 0, -1, 1.5, Number.NaN, '4096', null,
  ]) {
    assert.throws(
      () => readGhJson(['api', 'x'], { exec, maxBuffer }),
      RangeError,
      `maxBuffer=${String(maxBuffer)}`,
    );
  }
  assert.deepEqual(readGhJson(['api', 'x'], { exec, maxBuffer: 1 }), {});
});

test('non-ENOBUFS transport errors propagate unchanged', () => {
  const failure = Object.assign(new Error('gh exited 1'), { status: 1 });
  assert.throws(
    () => readGhJson(['api', 'x'], { exec: () => { throw failure; } }),
    (error) => error === failure,
  );
});

test('a malformed response body fails closed', () => {
  assert.throws(
    () => readGhJson(['api', 'x'], { exec: () => '{"status":"ahe' }),
    SyntaxError,
  );
});
