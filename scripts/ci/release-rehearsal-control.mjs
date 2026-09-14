import { appendFileSync, mkdirSync, writeFileSync } from 'node:fs';
import { requireThat } from './release-policy.mjs';
import { positiveRehearsal, readOnlyClient, rehearsalContext } from './release-rehearsal.mjs';
import { runDenialProbes } from './release-rehearsal-probes.mjs';
import { provisionFixture } from './release-rehearsal-fixture.mjs';

const mode = process.argv[2];
const env = process.env;
let receipt = { kind: 'release-rehearsal-only', schema: 1, passed: false };
try {
  requireThat(['positive', 'fixture', 'probes'].includes(mode) && process.argv.length === 3, 'Invalid rehearsal command');
  const context = rehearsalContext(env);
  if (mode === 'positive') {
    requireThat(env.REHEARSAL_APP_TOKEN && env.REHEARSAL_APP_TOKEN !== env.GH_TOKEN,
      'Separate downscoped App credential required');
    receipt = await positiveRehearsal(readOnlyClient(env.REHEARSAL_APP_TOKEN),
      readOnlyClient(env.GH_TOKEN), context, {
        appId: env.RELEASE_PUBLISHER_APP_ID, reviewers: env.RELEASE_OWNER_APPROVED_REVIEWERS,
      });
    receipt.passed = true;
    requireThat(env.GITHUB_OUTPUT, 'Missing Actions receipt output');
    appendFileSync(env.GITHUB_OUTPUT, `inventory_digest=${receipt.inventoryDigest}\n`);
  } else if (mode === 'fixture') {
    receipt = await provisionFixture(env);
    if (receipt.passed) {
      requireThat(env.GITHUB_OUTPUT, 'Missing Actions receipt output');
      appendFileSync(env.GITHUB_OUTPUT, `inventory_digest=${receipt.inventoryDigest}\n`);
    }
  } else {
    requireThat(!env.REHEARSAL_APP_TOKEN && !env.RELEASE_PUBLISHER_TOKEN &&
      !env.RELEASE_REGISTRY_TOKEN && !env.RELEASE_REHEARSAL_FIXTURE_PRIVATE_KEY,
    'Probe job must not have publisher credentials');
    receipt = await runDenialProbes(env.GH_TOKEN, context, {
      requested: env.REHEARSAL_DENIAL_PROBES === 'true',
      actor: env.GITHUB_ACTOR, positiveDigest: env.REHEARSAL_POSITIVE_DIGEST,
    });
  }
} catch {
  receipt.passed = false;
  receipt.failure = 'Rehearsal failed closed; no publication authorized';
}
mkdirSync('.artifacts/release-rehearsal', { recursive: true });
writeFileSync('.artifacts/release-rehearsal/receipt.json', `${JSON.stringify(receipt, undefined, 2)}\n`, { mode: 0o600 });
console.log(receipt.passed ? 'Bounded rehearsal evidence recorded; not publication authorization' :
  'Bounded rehearsal failed; retain evidence and keep publication blocked');
process.exitCode = receipt.passed ? 0 : 1;
