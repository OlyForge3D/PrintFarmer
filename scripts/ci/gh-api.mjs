// Bounded JSON reader for `gh api` shared by the CI verifier CLIs
// (verify-squad-verdict.mjs, verify-epic-dependencies.mjs).
//
// Node's execFileSync defaults to a 1 MiB stdout buffer, which legitimate
// GitHub responses (notably `compare` across a moved default branch) exceed,
// aborting verification with `spawnSync gh ENOBUFS` (#2988). The limit is
// therefore explicit and bounded rather than unlimited: a response above it
// still throws, so callers keep failing closed and never treat a transport
// failure as evidence. A caller may only SHORTEN the limit, never enlarge it,
// mirroring clampedOverride in src/Web/ReactApp/scripts/typecheck-app-core.mjs.

import { execFileSync } from 'node:child_process';

export const ghApiMaxBuffer = 32 * 1024 * 1024;

export function readGhJson(
  args,
  { exec = execFileSync, maxBuffer = ghApiMaxBuffer } = {},
) {
  if (
    !Number.isSafeInteger(maxBuffer) ||
    maxBuffer <= 0 ||
    maxBuffer > ghApiMaxBuffer
  ) {
    throw new RangeError(
      `gh transport limit must be an integer in 1..${ghApiMaxBuffer} bytes.`,
    );
  }
  let output;
  try {
    output = exec('gh', args, {
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'inherit'],
      maxBuffer,
    });
  } catch (error) {
    if (error?.code === 'ENOBUFS') {
      throw new Error(
        `gh ${args.join(' ')} response exceeded the ${maxBuffer}-byte ` +
        'transport limit; refusing to verify from a truncated response.',
        { cause: error },
      );
    }
    throw error;
  }
  return JSON.parse(output);
}
