// Release-bound host-local recovery instructions (issue #3063).
//
// The release workflow generates this document from the release identity alone, signs it with the
// same keyless workflow identity as update-manifest.json, and publishes it next to the manifest. An
// offline bundle carries the original signed bytes. Because the document is fully derived from the
// signed identity, validation is byte equality with a freshly generated copy: nothing in it can
// introduce a command, argument or release that the signed identity does not already determine.
//
// The document names only fixed, argument-validated wrapper operations for this one release. It is
// guidance for an operator on a network-denied host, never rollout authorization.
import { requireThat } from './release-policy.mjs';

export const recoveryInstructionsName = 'offline-recovery-instructions.json';
export const recoveryInstructionsSignatureName = 'offline-recovery-instructions.sigstore.json';
export const recoveryInstructionsKind = 'printfarmer-offline-recovery-instructions';

const identityKeys = ['tag', 'version', 'channel', 'sourceBranch', 'sourceCommit', 'buildId', 'sequence'];

const bash = (...argv) => ['printfarmer-host-update.sh', ...argv];
const powershell = (...argv) => ['pwsh', '-File', 'printfarmer-host-update.ps1', ...argv];

export function recoveryInstructionsDocument(identity) {
  requireThat(identity && typeof identity === 'object' && !Array.isArray(identity) &&
    Object.keys(identity).sort().join() === [...identityKeys].sort().join() &&
    identityKeys.every(key => identity[key] !== undefined && identity[key] !== null),
  'Recovery instructions require a complete release identity');
  requireThat(['stable', 'insider'].includes(identity.channel), 'Recovery instructions channel must be stable or insider');
  const release = `${identity.channel}:${identity.version}`;
  const operations = [
    {
      id: 'offline-bundle-import',
      description: 'Verify the offline bundle against the operator-supplied trusted root, load its verified images, and record the decision.',
      bash: bash('import', '--bundle', '<bundle.tar>', '--channel', identity.channel, '--version', identity.version,
        '--trusted-root', '<trusted_root.json>', '--staging', '<new-staging-dir>', '--records', '<decision-records-dir>',
        '--operator', '<operator>'),
      powershell: powershell('import', '-Bundle', '<bundle.tar>', '-Channel', identity.channel, '-Version',
        identity.version, '-TrustedRoot', '<trusted_root.json>', '-Staging', '<new-staging-dir>', '-Records',
        '<decision-records-dir>', '-Operator', '<operator>'),
    },
    {
      id: 'host-update-status',
      description: 'Read the durable host-update journal for this release.',
      bash: bash('--config', '<host-update.json>', 'status', '--release', release, '--json'),
      powershell: powershell('-Config', '<host-update.json>', 'status', '-Release', release, '-Json'),
    },
    {
      id: 'host-update-recover-preview',
      description: 'Preview recovery of this release from the journal without changing the host.',
      bash: bash('--config', '<host-update.json>', 'recover', '--release', release, '--preview'),
      powershell: powershell('-Config', '<host-update.json>', 'recover', '-Release', release, '-Preview'),
    },
    {
      id: 'host-update-recover-confirm',
      description: 'Recover this release after reviewing the preview; the release id is retyped to confirm.',
      bash: bash('--config', '<host-update.json>', 'recover', '--release', release, '--confirm', release),
      powershell: powershell('-Config', '<host-update.json>', 'recover', '-Release', release, '-Confirm', release),
    },
  ];
  return Buffer.from(`${JSON.stringify({
    schema: 1,
    kind: recoveryInstructionsKind,
    release: Object.fromEntries(identityKeys.map(key => [key, identity[key]])),
    rolloutAuthorization: false,
    operations,
  }, undefined, 2)}\n`);
}

export function validateRecoveryInstructions(bytes, identity) {
  const expected = recoveryInstructionsDocument(identity);
  requireThat(Buffer.isBuffer(bytes) || bytes instanceof Uint8Array, 'Recovery instructions must be bytes');
  requireThat(Buffer.from(bytes).equals(expected),
    'Recovery instructions are not the exact release-bound instructions for this release identity');
  return JSON.parse(expected.toString('utf8'));
}
