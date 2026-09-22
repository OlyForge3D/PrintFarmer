import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, readFile, rename, rm, stat, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { acquireTransactionLock } from '../ralph-native-lock.mjs';

async function fixture(t) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'ralph-lock-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  return path.join(root, 'journal.lock');
}

test('transaction lock records diagnostic identity without a round token and excludes concurrent callers', async (t) => {
  const lock = await fixture(t);
  const before = ['exit', 'SIGTERM', 'SIGINT'].map((signal) => process.listenerCount(signal));
  const release = await acquireTransactionLock(lock, { requestType: 'research-plan', roundId: 'original-round' });
  const owner = JSON.parse(await readFile(lock, 'utf8'));
  assert.equal(owner.pid, process.pid);
  assert.equal(owner.roundId, 'original-round');
  assert.equal(owner.requestType, 'research-plan');
  assert.equal(owner.roundToken, undefined);
  await assert.rejects(acquireTransactionLock(lock, {}), /transaction lock exists/);
  assert.deepEqual(JSON.parse(await readFile(lock, 'utf8')), owner);
  await release();
  assert.deepEqual(['exit', 'SIGTERM', 'SIGINT'].map((signal) => process.listenerCount(signal)), before);
  await assert.rejects(stat(lock), { code: 'ENOENT' });
});

test('lock cleanup never unlinks a replacement identity', async (t) => {
  const lock = await fixture(t);
  const release = await acquireTransactionLock(lock, {});
  await rename(lock, `${lock}.retained`);
  await writeFile(lock, 'replacement', { mode: 0o600 });
  await assert.rejects(release(), /identity changed/);
  assert.equal(await readFile(lock, 'utf8'), 'replacement');
});

for (const signal of ['SIGTERM', 'SIGINT', 'SIGKILL']) {
  test(`real ${signal} interruption preserves journal ownership and handles the transaction lock safely`, {
    skip: process.platform === 'win32' ? 'Windows does not deliver POSIX signals; explicit lock recovery remains required.' : false,
    timeout: 15_000,
  }, async (t) => {
    const lock = await fixture(t);
    const journal = path.join(path.dirname(lock), 'journal.json');
    const pending = `${journal}.pending`;
    const retained = JSON.stringify({ roundOwners: { original: { invocationDigest: 'retained' } }, events: ['durable-intent'] });
    await writeFile(journal, retained, { mode: 0o600 });
    await writeFile(pending, 'interrupted-write-evidence', { mode: 0o600 });
    const moduleUrl = new URL('../ralph-native-lock.mjs', import.meta.url).href;
    const child = spawn(process.execPath, ['--input-type=module', '-e', `
      import { acquireTransactionLock } from ${JSON.stringify(moduleUrl)};
      await acquireTransactionLock(${JSON.stringify(lock)}, { requestType: 'research-plan', roundId: 'original' });
      process.send('locked');
      setInterval(() => {}, 1000);
    `], { stdio: ['ignore', 'ignore', 'pipe', 'ipc'] });
    t.after(() => { if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL'); });
    const exited = once(child, 'exit');
    const ready = await Promise.race([
      once(child, 'message'),
      exited.then(([code]) => { throw new Error(`Lock subprocess exited before acquisition: ${code}`); }),
    ]);
    assert.equal(ready[0], 'locked');
    await assert.rejects(acquireTransactionLock(lock, {}), /transaction lock exists/);
    child.kill(signal);
    const [code, receivedSignal] = await exited;
    assert.equal(await readFile(journal, 'utf8'), retained);
    assert.equal(await readFile(pending, 'utf8'), 'interrupted-write-evidence');
    if (signal === 'SIGKILL') {
      assert.equal(receivedSignal, signal);
      assert.equal(JSON.parse(await readFile(lock, 'utf8')).pid, child.pid);
      await assert.rejects(acquireTransactionLock(lock, {}), /transaction lock exists/);
    } else {
      assert.equal(code, signal === 'SIGTERM' ? 143 : 130);
      await assert.rejects(stat(lock), { code: 'ENOENT' });
      const release = await acquireTransactionLock(lock, {});
      await release();
    }
  });
}
