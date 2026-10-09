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

// Schema 2 (#2981) adds the network-denied path end to end: importing a bundle that binds a prior
// recovery set (packaged or local-reference), activating the imported release, and recovering it to
// that prior set. Schema 1 documents published before it remain valid, byte-bound evidence.
export const recoveryInstructionsSchemas = Object.freeze([1, 2]);
const currentSchema = 2;

const importBash = identity => ['import', '--config', '<host-update.json>', '--bundle', '<bundle.tar>', '--channel',
  identity.channel, '--version', identity.version, '--trusted-root', '<trusted_root.json>', '--trusted-root-approval',
  '<trusted-root-approval.json>', '--staging', '<new-staging-dir>', '--records', '<decision-records-dir>',
  '--operator', '<operator>'];
const importPowershell = identity => ['import', '-Config', '<host-update.json>', '-Bundle', '<bundle.tar>', '-Channel',
  identity.channel, '-Version', identity.version, '-TrustedRoot', '<trusted_root.json>', '-TrustedRootApproval',
  '<trusted-root-approval.json>', '-Staging', '<new-staging-dir>', '-Records', '<decision-records-dir>',
  '-Operator', '<operator>'];

function networkDeniedOperations(identity, release) {
  const staged = ['--config', '<host-update.json>', '--staging', '<staging-dir>', '--channel', identity.channel,
    '--trusted-root', '<trusted_root.json>'];
  const stagedPowershell = ['-Config', '<host-update.json>', '-Staging', '<staging-dir>', '-Channel', identity.channel,
    '-TrustedRoot', '<trusted_root.json>'];
  const recover = [...staged, '--protected-backup', '<protected-backup.json>', '--release', release];
  const recoverPowershell = [...stagedPowershell, '-ProtectedBackup', '<protected-backup.json>', '-Release', release];
  return [
    {
      id: 'offline-bundle-import-with-prior',
      description: 'Import a bundle that packages its prior recovery set; the operator-held protected-backup reference must equal the one the bundle binds.',
      bash: bash(...importBash(identity), '--protected-backup', '<protected-backup.json>'),
      powershell: powershell(...importPowershell(identity), '-ProtectedBackup', '<protected-backup.json>'),
    },
    {
      id: 'offline-bundle-import-with-local-prior',
      description: 'Import a bundle that references a prior recovery set held on this host.',
      bash: bash(...importBash(identity), '--prior-recovery-set', '<prior-recovery-set-dir>', '--protected-backup',
        '<protected-backup.json>'),
      powershell: powershell(...importPowershell(identity), '-PriorRecoverySet', '<prior-recovery-set-dir>',
        '-ProtectedBackup', '<protected-backup.json>'),
    },
    {
      id: 'offline-activate',
      description: 'Activate the imported release from its verified staging directory with preloaded images only.',
      bash: bash('activate', ...staged),
      powershell: powershell('activate', ...stagedPowershell),
    },
    {
      id: 'offline-recover-preview',
      description: 'Preview network-denied recovery of this release to the prior set bound in the same staged bundle.',
      bash: bash('recover-offline', ...recover, '--preview'),
      powershell: powershell('recover-offline', ...recoverPowershell, '-Preview'),
    },
    {
      id: 'offline-recover-confirm',
      description: 'Recover this release to its verified prior set after reviewing the preview; the release id is retyped to confirm.',
      bash: bash('recover-offline', ...recover, '--confirm', release),
      powershell: powershell('recover-offline', ...recoverPowershell, '-Confirm', release),
    },
  ];
}

export function recoveryInstructionsDocument(identity, schema = currentSchema) {
  requireThat(recoveryInstructionsSchemas.includes(schema), 'Unsupported recovery instructions schema');
  requireThat(identity && typeof identity === 'object' && !Array.isArray(identity) &&
    Object.keys(identity).sort().join() === [...identityKeys].sort().join() &&
    identityKeys.every(key => identity[key] !== undefined && identity[key] !== null),
  'Recovery instructions require a complete release identity');
  requireThat(['stable', 'insider'].includes(identity.channel), 'Recovery instructions channel must be stable or insider');
  const release = `${identity.channel}:${identity.version}`;
  const operations = [
    {
      id: 'offline-bundle-import',
      description: 'Verify the offline bundle against the operator-approved trusted root, admit it through the host replay store, load its verified images, and record the decision.',
      bash: bash(...importBash(identity)),
      powershell: powershell(...importPowershell(identity)),
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
    ...(schema >= 2 ? networkDeniedOperations(identity, release) : []),
  ];
  return Buffer.from(`${JSON.stringify({
    schema,
    kind: recoveryInstructionsKind,
    release: Object.fromEntries(identityKeys.map(key => [key, identity[key]])),
    rolloutAuthorization: false,
    operations,
  }, undefined, 2)}\n`);
}

// Byte equality with a regeneration for the document's own declared schema: an edited, reordered,
// re-signed or wrong-release copy never matches, and a supported older schema stays verifiable.
export function validateRecoveryInstructions(bytes, identity) {
  requireThat(Buffer.isBuffer(bytes) || bytes instanceof Uint8Array, 'Recovery instructions must be bytes');
  const candidate = Buffer.from(bytes);
  const expected = recoveryInstructionsSchemas.map(schema => recoveryInstructionsDocument(identity, schema))
    .find(document => document.equals(candidate));
  requireThat(expected !== undefined,
    'Recovery instructions are not the exact release-bound instructions for this release identity');
  return JSON.parse(expected.toString('utf8'));
}
