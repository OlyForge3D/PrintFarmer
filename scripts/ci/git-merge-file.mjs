// Real-Git three-way merge oracle for the squad verdict carry-forward proof
// (issue #2801, finding R3365-01). Kept out of squad-verdict-gate.mjs so that
// module stays free of I/O; the gate consumes this function's result as
// mandatory evidence.

import { spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

/** Upper bound on merge-file output; anything larger fails closed. */
const maxOutputBytes = 64 * 1024 * 1024;

/**
 * Run `git merge-file -p <ours> <base> <theirs>` over latin1-encoded strings
 * (one char per byte, so bytes round-trip exactly) and report Git's verdict.
 *
 * Returns `{ clean, output }`: `clean` is true only when Git exited 0 (no
 * conflicts); `output` is the merged text, latin1-decoded. Any failure to run
 * Git, any signal, any non-zero exit (conflict count or error) yields
 * `{ clean: false, output: null }` so callers fail closed. A sanitised
 * environment keeps user/system Git config from influencing the result.
 */
export function gitMergeFile({ base, ours, theirs } = {}) {
  if (![base, ours, theirs].every((text) => typeof text === 'string')) {
    return { clean: false, output: null };
  }
  const dir = mkdtempSync(join(tmpdir(), 'squad-merge-file-'));
  try {
    const paths = { ours: join(dir, 'ours'), base: join(dir, 'base'), theirs: join(dir, 'theirs') };
    writeFileSync(paths.ours, ours, 'latin1');
    writeFileSync(paths.base, base, 'latin1');
    writeFileSync(paths.theirs, theirs, 'latin1');
    const result = spawnSync(
      'git',
      ['merge-file', '-p', paths.ours, paths.base, paths.theirs],
      {
        cwd: dir,
        maxBuffer: maxOutputBytes,
        env: {
          ...process.env,
          GIT_CONFIG_NOSYSTEM: '1',
          GIT_CONFIG_GLOBAL: process.platform === 'win32' ? 'NUL' : '/dev/null',
        },
      },
    );
    if (result.error || result.signal || result.status !== 0 || !Buffer.isBuffer(result.stdout)) {
      return { clean: false, output: null };
    }
    return { clean: true, output: result.stdout.toString('latin1') };
  } catch {
    return { clean: false, output: null };
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}
