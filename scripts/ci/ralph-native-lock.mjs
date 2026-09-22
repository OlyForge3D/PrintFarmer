import { fstatSync, lstatSync, unlinkSync } from 'node:fs';
import { open } from 'node:fs/promises';

// This is a process-local transaction lock, not the durable mailbox round gate.
export async function acquireTransactionLock(lockPath, context) {
  let handle;
  try {
    handle = await open(lockPath, 'wx', 0o600);
  } catch (error) {
    if (error.code !== 'EEXIST') throw error;
    throw new Error('Private transaction lock exists; reconcile interrupted write, never steal by age.');
  }
  const identity = fstatSync(handle.fd);
  const removeOwnedLock = () => {
    const current = lstatSync(lockPath);
    if (current.dev !== identity.dev || current.ino !== identity.ino || !current.isFile()) {
      throw new Error('Transaction lock identity changed; refusing to remove another owner.');
    }
    unlinkSync(lockPath);
  };
  const onExit = () => {
    try { removeOwnedLock(); }
    catch (error) { process.stderr.write(`Native Ralph lock cleanup failed: ${error.message}\n`); }
  };
  const onTerm = () => process.exit(143);
  const onInt = () => process.exit(130);
  process.once('exit', onExit);
  process.once('SIGTERM', onTerm);
  process.once('SIGINT', onInt);
  const release = async () => {
    try {
      await handle.close();
      removeOwnedLock();
    } finally {
      process.removeListener('exit', onExit);
      process.removeListener('SIGTERM', onTerm);
      process.removeListener('SIGINT', onInt);
    }
  };
  try {
    await handle.writeFile(JSON.stringify({
      version: 1, pid: process.pid, acquiredAt: new Date().toISOString(), ...context,
    }));
    await handle.sync();
  } catch (error) {
    await release();
    throw error;
  }
  return release;
}
