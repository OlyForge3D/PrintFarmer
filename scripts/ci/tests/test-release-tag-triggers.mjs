import assert from 'node:assert/strict';
import {
  copyFileSync, existsSync, linkSync, mkdirSync, readFileSync, readdirSync, rmSync,
  symlinkSync, writeFileSync,
} from 'node:fs';
import { execFileSync, spawnSync } from 'node:child_process';
import { X509Certificate } from 'node:crypto';
import { dirname, resolve } from 'node:path';
import test from 'node:test';
import { sourceReviewFixture } from './fixtures/release-source-review.mjs';
import { canonicalValidationChecks } from '../canonical-qualification.mjs';
import {
  abandon, abandonmentAuthorization, admit, advance as advancePolicy, allocationKey, compareVersions, components, hash, identityLabels, signedReleasePointer,
  parseTag, parseVersionFile, reserve as reserveRelease, transact, validateCandidate, validateCompleteSet,
  validateLedger, verifyConsumer, verifyTag, verifyProtectionEvidence, hotfixReasonDigest, ReleasePolicyError,
  validateRecord, migrateLegacyLedger, releaseBuildChecks, releaseReviewStatus, releaseRequiredChecks, publisherWorkflowIdentity,
  loadReleaseMetadata, loadReleaseTrustPolicy, releaseManifest, releaseManifestEnvelope, validateReleaseManifest, validateReleaseManifestBytes, validateReleaseManifestEnvelope,
} from '../release-policy.mjs';
import { ensureSourceTag, githubClient, githubRequestUrl, gitLedger, publicLedger, publicLedgerFields, readTag, readVersion, verifyProtection,
  parseGithubTimestamp, verifyStableQualification, verifyReleaseChecks } from '../release-github.mjs';
import { buildMetadata, emitBuildIdentity } from '../release-metadata.mjs';
import { runReleaseControl, output } from '../release-control.mjs';
import {
  inspectCompleteSet, plannedReleaseAliases, publishImmutableTags, publishReleaseAliases,
  registryTagInspection,
} from '../release-set.mjs';
import {
  authorizationPath, authorizationBundle, manifestEnvelopeBundle, manifestEnvelopePath, manifestPath, privateSetPath,
  publicAuthorization, verifyAuthorization, writeAuthorization, writeAuthorizationSet, writePublicSet,
  emitPublicReleaseAssets, readPrivateJson, readReleaseManifest,
} from '../release-authorization.mjs';
import {
  qualificationJobNamespace, qualificationLifetimeMs, qualificationPath,
  qualifyTransaction, selectTransaction, validateQualificationReceipt,
} from '../release-transaction.mjs';
import { publicIdentity, publicIdentityFields } from '../../../src/Web/ReactApp/public-release-identity.mjs';
import { changelogEntry, releaseNotes, validateReleaseNotesMetadata } from '../release-notes.mjs';
import { normalizeEvidence } from '../release-evidence.mjs';

const sha = 'a'.repeat(40);
const newerSha = 'b'.repeat(40);
const anchor = 'c'.repeat(40);
const workflowControlSha = '9'.repeat(40);
const currentCanonicalSha = '8'.repeat(40);
const created = '2026-09-14T22:40:00.000Z';
const releaseNotesHash = 'd'.repeat(64);
const cryptoCertificate = `-----BEGIN CERTIFICATE-----
MIIDITCCAgmgAwIBAgIUJgCnA8pimf4TtHhn54DkRCgUikowDQYJKoZIhvcNAQEL
BQAwIDEeMBwGA1UEAwwVcmVsZWFzZS1ldmlkZW5jZS10ZXN0MB4XDTI2MDkxNDIy
NDc0NFoXDTI3MDkxNDIyNDc0NFowIDEeMBwGA1UEAwwVcmVsZWFzZS1ldmlkZW5j
ZS10ZXN0MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAtZ3XWRa4DU9i
CHWD3tUhj5Kf2dpXE3+luZikC1/FQti6ZrxeT+6nLihTCdroFpNoXQrT2O3neUFI
37OOiygQ6Cd8WSRJ+CLt6VIUuTu6ntWbLR3OHEv2wBCAVsL1HWoiUoAuKoOVZEHq
HwHU+z6BCw/1MhbGDdzgc6tyirAN1WB5NYB8HXjnpfY4DLIvjYr7bLhn2ep5r1QH
mtM4pN67lgpEYxc6JLMvu5V+Dg3GU6VEX34OGXdmBT3xEQ7T54XJtFgU05OtRHpm
ME8rqz7U2uGWlPwPKmLin/Hb+o05UskhCNM5x/l/HnD0ggBVPFnhCNaDO18QJs0m
7CbykcQh+wIDAQABo1MwUTAdBgNVHQ4EFgQUn1N4X6p6NnttpzKqzDvNu/lbyDww
HwYDVR0jBBgwFoAUn1N4X6p6NnttpzKqzDvNu/lbyDwwDwYDVR0TAQH/BAUwAwEB
/zANBgkqhkiG9w0BAQsFAAOCAQEAq2mAhPcSnYNlCNYPX/iCn0GSg32RwYUdi2Ap
2L0lmztFLny0pKiYcwKwIK35+pYSVOr28vRj16SBmZFPdAjUGKnyMVbOKGR6Zggj
v7m19BoUidStAqQ32L6VT1Qr9CpbqFERXUEzn6AfnP0QoPo6WctYia+sa+FjYlk9
rQlctU3q0xs/y0h/ZtBMtiCSqoWd/pSr21YrXPLUFQPPKpE1eYfh1Zoc4sWY5xMF
xhbqlANI3r0ql7hOlLRKsnswTHq4h1xJ/81+BTahCkwGIbvBnO2ddVdZfNeAkNS4
JvcWVV+jlxUIUkvyi8jjlUzq/l/os6B8crubKZ9z++J4rrlAoA==
-----END CERTIFICATE-----`;
const context = (overrides = {}) => ({
  repository: 'OlyForge3D/PrintFarmer', event: 'workflow_dispatch',
  ref: 'refs/heads/development', eventSha: sha,
  workflowIdentity: 'OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/development',
  workflowSha: sha, workflowBranch: 'development', buildId: '42', buildAttempt: '1',
  channel: 'insider', ...overrides,
});

const releaseMetadataFixture = version => {
  const { trustPolicy, ...metadata } = loadReleaseMetadata('0.2.3');
  return { ...metadata, version };
};
const cryptoEvidenceFixture = set => {
  const subject = digest => {
    const predicate = JSON.stringify({ SPDXID: 'SPDXRef-DOCUMENT' });
    const createdAt = Date.parse(set.identity.created);
    const integratedTime = Number.isFinite(createdAt)
      ? Math.max(Math.floor(createdAt / 1000) + 60, 1789426200)
      : 1789426200;
    const optional = { Subject: publisherWorkflowIdentity, Issuer: 'https://token.actions.githubusercontent.com',
      certificate: cryptoCertificate,
      Bundle: { Payload: { integratedTime, canonicalizedBody: 'proof', signature: 'native-signature' } } };
    const signatureBytes = JSON.stringify([{ critical: { image: { 'docker-manifest-digest': digest } }, optional }]);
    const attestationBytes = JSON.stringify([{ payload: Buffer.from(JSON.stringify({
      subject: [{ digest: { sha256: digest.slice(7) } }], predicate: JSON.parse(predicate),
    })).toString('base64'), optional }]);
    return {
      signatureBytes,
      attestationBytes,
      predicateBytes: predicate,
      signatureVerificationTime: new Date((integratedTime + 300) * 1000).toISOString(),
      attestationVerificationTime: new Date((integratedTime + 300) * 1000).toISOString(),
      signatureBundleBytes: JSON.stringify([{
        mediaType: 'application/vnd.dev.sigstore.bundle.v0.3+json',
        verificationMaterial: {
          certificate: { rawBytes: new X509Certificate(cryptoCertificate).raw.toString('base64') },
          tlogEntries: [{ integratedTime, canonicalizedBody: 'proof' }],
        },
        messageSignature: {
          messageDigest: { algorithm: 'SHA2_256', digest: Buffer.from(digest.slice(7), 'hex').toString('base64') },
          signature: 'native-signature',
        },
      }]),
      attestationBundleBytes: JSON.stringify([{
        mediaType: 'application/vnd.dev.sigstore.bundle.v0.3+json',
        verificationMaterial: {
          certificate: { rawBytes: new X509Certificate(cryptoCertificate).raw.toString('base64') },
          tlogEntries: [{ integratedTime, canonicalizedBody: 'proof' }],
        },
        dsseEnvelope: { payload: Buffer.from(JSON.stringify({
          subject: [{ digest: { sha256: digest.slice(7) } }], predicate: JSON.parse(predicate),
        })).toString('base64'), payloadType: 'application/vnd.in-toto+json',
        signatures: [{ sig: 'native-signature' }] },
      }]),
    };
  };
  const services = Object.fromEntries(Object.entries(set.images).map(([service, image]) => [
    service, {
      index: normalizeEvidence({ subject: image.digest, trust: {
        policy: loadReleaseTrustPolicy(), releaseId: set.identity.releaseId, createdTime: created,
      }, ...subject(image.digest) }),
      platforms: Object.fromEntries(Object.entries(image.platforms).map(([platform, value]) => [
        platform, normalizeEvidence({ subject: value.digest, platform, trust: {
          policy: loadReleaseTrustPolicy(), releaseId: set.identity.releaseId, createdTime: created,
        }, ...subject(value.digest) }),
      ])),
    },
  ]));
  const rawCreatedTime = set.identity.created;
  const createdAt = Date.parse(rawCreatedTime);
  const createdTime = Number.isFinite(createdAt) ? rawCreatedTime : created;
  const verificationTime = new Date(Math.max(
    Number.isFinite(createdAt) ? createdAt + 10 * 60_000 : 0, 1789426200 * 1000,
  )).toISOString();
  return { schema: 1, createdTime, verificationTime, services, sha256: hash({
    schema: 1, createdTime, verificationTime, services,
  }) };
};

const signedManifest = (identity, set) => {
  const manifest = releaseManifest(identity, set, undefined, releaseNotesHash,
    releaseMetadataFixture(identity.baseVersion), cryptoEvidenceFixture(set));
  const envelope = releaseManifestEnvelope(manifest);
  return { serializedManifest: JSON.stringify(manifest), serializedEnvelope: JSON.stringify(envelope) };
};

const advance = (ledger, identity, set, currentHead, expectedPointer, signed = signedManifest(identity, set)) => {
  const pointer = signedReleasePointer(identity, signed);
  const legacySetHash = publicSetHash(set);
  const priorPointer = Object.values(ledger.pointers)
    .find(candidate => ledger.reservations[candidate.allocationKey]?.setHash === expectedPointer);
  const result = advancePolicy(ledger, identity, set, signed, currentHead,
    expectedPointer === legacySetHash ? pointer.manifestEnvelopeSha256
      : priorPointer?.manifestEnvelopeSha256 ?? expectedPointer);
  return { ...result, setHash: legacySetHash };
};

test('changelog release entry parser handles final, multiple, and literal z content', () => {
  const entry = heading => `### Features\n\n${heading}\n\n### Fixes\n\nNone.\n\n### Breaking changes\n\nN/A`;
  assert.equal(changelogEntry(`## [1.2.3]\n\n${entry('z')}`, '1.2.3'), entry('z'));
  assert.equal(changelogEntry(`## [1.2.3]\n\n${entry('z')}\n\n## [1.2.4]\n\n${entry('later')}`, '1.2.3'), entry('z'));
  assert.equal(changelogEntry(`## 1.2.3 - 2026-09-14\n\n${entry('dated')}`, '1.2.3'), entry('dated'));
  assert.equal(changelogEntry(`## [1.2.3] - 2026-09-14\n\n${entry('bracketed date')}`, '1.2.3'), entry('bracketed date'));
  for (const version of ['1.2.3|1.2.4', '1.2.3.*', '1.2.3\\d', '[1.2.3]']) {
    assert.throws(() => changelogEntry(`## [1.2.4]\n\n${entry('unrelated')}`, version));
  }
  for (const heading of ['## [1.2.3|1.2.4]', '## [1.2.3-malformed]', '## [1.2.3-insider.1]']) {
    assert.throws(() => changelogEntry(`${heading}\n\n${entry('untrusted')}`, '1.2.3'),
      /require one 1\.2\.3 CHANGELOG entry/);
  }
  assert.throws(() => changelogEntry(
    `## [1.2.3]\n\n${entry('first')}\n\n## 1.2.3 - 2026-09-14\n\n${entry('second')}`,
    '1.2.3',
  ), /require one 1\.2\.3 CHANGELOG entry/);
});

test('release notes derive bounded merged PRs and mandatory version-controlled operational metadata', () => {
  const metadata = {
    schema: 1, version: '1.2.3', compatibility: 'API 1.x is required.', migration: 'Run provider migration.',
    downtime: 'Restart services.', backup: 'Create a backup.', recovery: 'Restore the backup.',
  };
  const notes = releaseNotes({
    version: '1.2.3-insider.9', sourceCommit: sha, previousTag: 'v1.2.2',
    pullRequests: [{ number: 42, title: 'Release-safe change', url: 'https://github.com/OlyForge3D/PrintFarmer/pull/42' }],
    changelog: '### Features\n\n- New capability.\n\n### Fixes\n\n- Fixed behavior.\n\n### Breaking changes\n\n- None.',
    metadata,
  });

  assert.match(notes, /Release range: v1\.2\.2\.\.\./);
  assert.match(notes, /#42/);
  assert.match(notes, /Run provider migration/);
  for (const body of ['None.', 'N/A']) {
    assert.doesNotThrow(() => releaseNotes({
      version: '1.2.3', sourceCommit: sha, previousTag: 'v1.2.2',
      pullRequests: [{ number: 1, title: 'Entry', url: 'https://github.com/OlyForge3D/PrintFarmer/pull/1' }],
      changelog: `### Features\n\n${body}\n\n### Fixes\n\n${body}\n\n### Breaking changes\n\n${body}`, metadata,
    }));
  }
  for (const field of ['compatibility', 'migration', 'downtime', 'backup', 'recovery']) {
    assert.throws(() => validateReleaseNotesMetadata({ ...metadata, [field]: '' }, '1.2.3'),
      new RegExp(`requires ${field}`));
  }
  assert.throws(() => releaseNotes({ version: '1.2.3', sourceCommit: sha, previousTag: 'v1.2.2',
    pullRequests: [], changelog: 'entry', metadata }), /at least one merged pull request/);
});

test('release notes escape every Markdown-significant backslash and delimiter in PR titles', () => {
  const notes = releaseNotes({
    version: '1.2.3', sourceCommit: sha, previousTag: 'v1.2.2',
    pullRequests: [{
      number: 42,
      title: 'Path \\ `code` *bold* _emphasis_ ~strike~ [link] <tag> & entity',
      url: 'https://github.com/OlyForge3D/PrintFarmer/pull/42',
    }],
    changelog: 'entry',
    metadata: {
      compatibility: 'Compatible.', migration: 'Migrate.', downtime: 'Restart.', backup: 'Backup.', recovery: 'Recover.',
    },
  });
  assert.ok(notes.includes('Path \\\\ \\`code\\` \\*bold\\* \\_emphasis\\_ \\~strike\\~ \\[link\\] \\<tag\\> \\& entity'));
});

test('release notes reject URLs that do not exactly bind a title to its PR number and repository', () => {
  assert.throws(() => releaseNotes({
    version: '1.2.3', sourceCommit: sha, previousTag: 'v1.2.2',
    pullRequests: [{
      number: 42,
      title: 'Release-safe change',
      url: 'https://github.com/OlyForge3D/PrintFarmer/pull/42?unexpected=1',
    }],
    changelog: 'entry',
    metadata: {
      compatibility: 'Compatible.', migration: 'Migrate.', downtime: 'Restart.', backup: 'Backup.', recovery: 'Recover.',
    },
  }), /malformed merged pull request data/);
});

test('canonical schema-3 release metadata generates release notes through the shared notes adapter', () => {
  const metadata = JSON.parse(readFileSync('release-metadata/0.2.3.json', 'utf8'));
  assert.deepEqual(validateReleaseNotesMetadata(metadata, '0.2.3'), metadata.notes);
  const notes = releaseNotes({
    version: '0.2.3', sourceCommit: sha, previousTag: 'v0.2.2',
    pullRequests: [{ number: 2660, title: 'Signed release publication', url: 'https://github.com/OlyForge3D/PrintFarmer/pull/2660' }],
    changelog: '### Features\n\n- Signed release publication.\n\n### Fixes\n\n- None.\n\n### Breaking changes\n\n- None.',
    metadata,
  });
  assert.match(notes, /Only the declared source release IDs/);
  assert.match(notes, /verified provider backup/);
});

test('main-equivalent schema-3 metadata validation passes its closed notes adapter to release notes', () => {
  const rawMetadata = JSON.parse(readFileSync('release-metadata/0.2.3.json', 'utf8'));
  const validatedNotes = validateReleaseNotesMetadata(rawMetadata, '0.2.3');
  const notes = releaseNotes({
    version: '0.2.3', sourceCommit: sha, previousTag: 'v0.2.2',
    pullRequests: [{ number: 2660, title: 'Signed release publication', url: 'https://github.com/OlyForge3D/PrintFarmer/pull/2660' }],
    changelog: '### Features\n\n- Signed release publication.\n\n### Fixes\n\n- None.\n\n### Breaking changes\n\n- None.',
    metadata: validatedNotes,
  });
  assert.match(notes, /Only the declared source release IDs/);
  assert.match(notes, /Restore the verified provider backup/);
  assert.throws(() => releaseNotes({
    version: '0.2.3', sourceCommit: sha, previousTag: 'v0.2.2',
    pullRequests: [{ number: 2660, title: 'Signed release publication', url: 'https://github.com/OlyForge3D/PrintFarmer/pull/2660' }],
    changelog: 'entry', metadata: { ...validatedNotes, inferred: 'no' },
  }), /unknown or missing fields/);
});

test('closed release metadata is canonical, complete, and digest-bound into the manifest', () => {
  const metadata = releaseMetadataFixture('1.2.3');
  const identity = record();
  const set = completeSet(identity);
  const manifest = releaseManifest(identity, set, undefined, releaseNotesHash, metadata, cryptoEvidenceFixture(set));
  assert.equal(manifest.evidence.releaseMetadata.sha256, hash(metadata));
  assert.equal(manifest.compatibility.releaseMetadata.version, identity.baseVersion);
  for (const mutate of [
    value => { value.migrations.postgresql.AppDbContext = 'current-head'; },
    value => { value.operations.ordered = ['pull', 'verify']; },
    value => { value.rollback.class = 'supported'; },
  ]) {
    const changed = structuredClone(metadata);
    mutate(changed);
    const path = resolve('.artifacts', `invalid-release-metadata-${process.pid}.json`);
    mkdirSync(dirname(path), { recursive: true });
    writeFileSync(path, JSON.stringify(changed));
    assert.throws(() => loadReleaseMetadata('1.2.3', path), ReleasePolicyError);
    rmSync(path, { force: true });
  }
});

test('release manifest bytes and envelope reject canonicality and binding substitutions', () => {
  const identity = record();
  const set = completeSet(identity);
  const manifest = releaseManifest(identity, set, undefined, releaseNotesHash,
    releaseMetadataFixture(identity.baseVersion), cryptoEvidenceFixture(set));
  const envelope = releaseManifestEnvelope(manifest);
  const serialized = JSON.stringify(manifest);
  assert.deepEqual(validateReleaseManifestBytes(serialized, envelope), manifest);
  assert.throws(() => validateReleaseManifestBytes(`${serialized}\n`, envelope), ReleasePolicyError);
  assert.throws(() => validateReleaseManifestBytes(JSON.stringify({
    ...manifest, lifecycle: { ...manifest.lifecycle, releaseId: 'insider:9.9.9-insider.1' },
  }), envelope), ReleasePolicyError);
  assert.throws(() => validateReleaseManifestEnvelope({
    ...envelope, manifestSha256: 'f'.repeat(64),
  }, manifest), ReleasePolicyError);
  assert.throws(() => validateReleaseManifest({
  ...manifest,
  evidence: {
    ...manifest.evidence,
    services: {
      ...manifest.evidence.services,
      api: {
        ...manifest.evidence.services.api,
        index: { ...manifest.evidence.services.api.index, subject: `sha256:${'f'.repeat(64)}` },
      },
    },
  },
  }), /Evidence subject\/platform mismatch/);
  for (const mutate of [
    value => { value.evidence.trust.workflowCommit = newerSha; },
    value => { value.evidence.trust.policyDigest = 'f'.repeat(64); },
    value => { value.evidence.trust.createdTime = '2026-09-14T00:00:00.000Z'; },
  ]) {
    const changed = structuredClone(manifest);
    mutate(changed);
    assert.throws(() => validateReleaseManifest(changed), /Release trust binding mismatch/);
  }
  const changedDigest = structuredClone(manifest);
  changedDigest.evidence.trust.createdTime = identity.buildTime;
  changedDigest.evidence.cryptoEvidence.sha256 = 'f'.repeat(64);
  assert.throws(() => validateReleaseManifest(changedDigest),
    /Invalid release trust evidence fields|Immutable crypto evidence digest mismatch/);
});

test('every docker publisher bash run block parses', () => {
  const workflow = readFileSync('.github/workflows/docker-publish.yml', 'utf8');
  const runBlocks = [...workflow.matchAll(/^        run: \|\r?\n((?:          .*(?:\r?\n|$))*)/gm)]
    .map(([, block]) => block.replace(/^          /gm, ''));
  assert.ok(runBlocks.length > 0, 'Expected Docker publisher Bash run blocks');
  const shell = process.platform === 'win32' ? 'C:\\Program Files\\Git\\bin\\bash.exe' : 'bash';
  for (const runBlock of runBlocks) {
    const result = spawnSync(shell, ['-n'], { input: runBlock, encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
  }
});

test('transaction-bound release operations reject missing transactions before verification or network access', async () => {
  for (const operation of ['admit', 'authorize', 'advance', 'consume', 'preflight']) {
    let verified = false;
    await assert.rejects(runReleaseControl(operation, {
      GH_TOKEN: 'github-fixture',
      RELEASE_PUBLISHER_TOKEN: 'publisher-fixture',
    }, () => { verified = true; }), /Missing release transaction/);
    assert.equal(verified, false, operation);
  }
});

test('admission and authorization reject blank, malformed and invalid transactions before API or artifact mutation', async () => {
  const previous = globalThis.fetch;
  const cwd = process.cwd();
  const root = resolve('.artifacts', `invalid-authorization-transaction-${process.pid}`);
  mkdirSync(root, { recursive: true });
  process.chdir(root);
  globalThis.fetch = () => assert.fail('Invalid transaction must fail before network access');
  try {
    for (const operation of ['admit', 'authorize']) {
      for (const [transaction, expected] of [
        ['', /Missing release transaction/],
        ['   ', /Missing release transaction/],
        ['{', /^Release transaction is malformed$/],
        ['null', /release transaction/],
        ['{}', /release transaction/],
        [JSON.stringify({ kind: 'release-transaction', schema: 2 }), /release transaction/],
      ]) {
        await assert.rejects(runReleaseControl(operation, {
          GH_TOKEN: 'github-fixture',
          RELEASE_PUBLISHER_TOKEN: 'publisher-fixture',
          RELEASE_TRANSACTION: transaction,
        }), error => error instanceof ReleasePolicyError && expected.test(error.message));
        assert.equal(existsSync(authorizationPath), false);
        assert.equal(existsSync(qualificationPath), false);
      }
    }
  } finally {
    globalThis.fetch = previous;
    process.chdir(cwd);
    rmSync(root, { recursive: true, force: true });
  }
});
const state = () => ({ schema: 1, anchor, counter: '0', channelSequences: { insider: '0', stable: '0' }, reservations: {}, identities: {}, pointers: {}, stages: {}, qualifications: {} });
const hotfixQualification = () => ({
  schema: 1, sourceCommit: sha, reviewed: true, tests: true, compatibility: true,
  migrations: true, recovery: true, mode: 'hotfix',
  reasonSha256: hotfixReasonDigest('Emergency recovery fix cannot wait for the next insider qualification.'),
});
function fixtureProtection(channel) {
  const payload = {
    schema: 5, repository: context().repository, channel, branch: channel === 'stable' ? 'main' : 'development',
    verifiedAt: created, policyProfile: 'printfarmer-release-protection/v4',
    approvalMode: 'separation-of-duties', approvalAssurance: 'non-self-review-enforced',
    claims: { ...Object.fromEntries([
      'branchDeletionBlocked', 'branchRewritesBlocked', 'pullRequestRequired',
      'branchBypassBlocked', 'conversationResolutionRequired', 'selfAttestedReviewRequired',
      'requiredChecksEnforced', 'canonicalEnvironmentBranchOnly', 'manualApprovalRequired',
      'environmentAdminBypassBlocked',
      'canonicalTagsImmutable', 'ledgerContinuityProtected', 'exclusiveApprovedPublisher',
    ].map(claim => [claim, true])), codeOwnerApprovalRequired: true, nonSelfApprovalRequired: true },
  };
  return { ...payload, policyDigest: hash(payload) };
}
const reserve = (ledger, admitted, timestamp, protection = fixtureProtection(admitted.channel), qualification) =>
  reserveRelease(ledger, admitted, timestamp, protection, qualification);
const abandonmentApproval = (identity, overrides = {}) => ({
  runId: '42', runAttempt: '1', jobId: '900', environment: 'release-insider',
  targetReservation: identity.allocationKey, approvedAt: '2026-09-14T22:41:00.000Z', ...overrides,
});
const admission = (overrides = {}) => admit(context(overrides), overrides.eventSha || sha, 'v1.2.3\n', '1.2.2');
const stableAdmission = (baseVersion = '1.2.3', overrides = {}) => admit(context({
  channel: 'stable', ...overrides,
}), sha, `v${baseVersion}\n`);
function stableFloorLedger(kind, floor) {
  const ledger = state();
  ledger.qualifications[sha] = hotfixQualification();
  ledger.lastHistoricalStable = kind === 'historical' ? floor : '1.2.0';
  if (kind === 'pointer') {
    const stable = reserve(ledger, stableAdmission(floor), created, undefined, hotfixQualification()).record;
    advance(ledger, stable, completeSet(stable), sha, '');
  }
  return ledger;
}
const record = (ledger = state(), overrides = {}) => reserve(ledger, admission(overrides), created).record;
const completeSet = identity => ({
  schema: 1, identity, managedEligible: false,
  images: Object.fromEntries(Object.entries(components).map(([name, platforms], index) => [name, {
    digest: `sha256:${String(index + 1).repeat(64)}`,
    platforms: Object.fromEntries(platforms.map(platform => [platform, {
      digest: `sha256:${String(index + 1).repeat(64)}`, labels: identityLabels(identity),
    }])),
  }])),
});
const publicSetHash = set => hash(writePublicSet(set.identity, set));
const releaseTransaction = identity => ({
  kind: 'release-transaction',
  schema: 2,
  repository: identity.repository,
  channel: identity.channel,
  sourceBranch: identity.sourceBranch,
  sourceCommit: identity.sourceCommit,
  observedBranchHead: identity.authorizedBranchHead,
  workflowIdentity: identity.workflowIdentity,
  workflowCommit: identity.workflowCommit,
  runId: identity.buildId,
  runAttempt: '1',
  approvalMode: identity.protection.approvalMode,
});
const qualificationJobs = [
  ...releaseRequiredChecks.filter(name => name !== releaseReviewStatus),
  ...canonicalValidationChecks,
];

function writeQualificationFixture(transaction) {
  const now = Date.now();
  const checkedAt = new Date(now).toISOString();
  const startedAt = new Date(now - 4 * 60_000).toISOString();
  const completedAt = new Date(now - 2 * 60_000).toISOString();
  const receipt = {
    kind: 'release-qualification',
    schema: 2,
    transaction,
    checkedAt,
    expiresAt: new Date(now + qualificationLifetimeMs).toISOString(),
    qualifiedBranchHead: transaction.observedBranchHead,
    run: {
      id: transaction.runId,
      attempt: transaction.runAttempt,
      checkSuiteId: '100',
      url: `https://github.com/${transaction.repository}/actions/runs/${transaction.runId}`,
      startedAt,
      completedAt,
    },
    jobs: qualificationJobs.map(name => ({
      name: `${qualificationJobNamespace} / ${name}`,
      url: `https://github.com/${transaction.repository}/actions/runs/${transaction.runId}`,
      startedAt,
      completedAt,
      checkCompletedAt: completedAt,
    })),
    sourceEvidence: {
      sourceCommit: transaction.sourceCommit,
      reviewUrl: `https://github.com/${transaction.repository}/actions/runs/30`,
      collectedAt: checkedAt,
      checks: releaseRequiredChecks.map(context => ({
        context,
        ...(context === releaseReviewStatus
          ? { statusId: 1, createdAt: completedAt, updatedAt: completedAt }
          : { checkId: 1, completedAt }),
      })),
    },
  };
  validateQualificationReceipt(receipt, transaction, now);
  mkdirSync(dirname(qualificationPath), { recursive: true });
  writeFileSync(qualificationPath, JSON.stringify(receipt));
}

async function runFixtureControl(operation, fixture, env = fixture.env, verify) {
  if (operation === 'authorize') {
    writeQualificationFixture(JSON.parse(env.RELEASE_TRANSACTION));
  }
  return runReleaseControl(operation, env, verify);
}
function promotionQualification(identity, set, sourceCommit = newerSha) {
  const tree = { schema: 1, originTree: 'd'.repeat(40), sourceTree: 'd'.repeat(40), metadataChanges: [] };
  const pointer = signedReleasePointer(identity, signedManifest(identity, set));
  return {
    schema: 1, sourceCommit, reviewed: true, tests: true, compatibility: true,
    migrations: true, recovery: true, mode: 'promotion',
    promotionOrigin: { allocationKey: identity.allocationKey, releaseId: identity.releaseId,
      sourceCommit: identity.sourceCommit, manifestSha256: pointer.manifestSha256,
      envelopeSha256: pointer.envelopeSha256 },
    treeEvidence: { ...tree, diffSha256: hash(tree) },
  };
}

function promotionApi(options = {}) {
  const treeSha = 'd'.repeat(40);
  const leaves = [
    { path: 'VERSION', type: 'blob', mode: '100644', sha: 'e'.repeat(40) },
    { path: 'src/api.cs', type: 'blob', mode: '100644', sha: 'f'.repeat(40) },
  ];
  let selected;
  return async (endpoint, method = 'GET') => {
    assert.equal(method, 'GET', 'Qualification is read-only');
    if (endpoint.startsWith('git/commits/')) {
      selected = endpoint.split('/').at(-1);
      return { sha: selected, tree: { sha: selected === newerSha && options.sourceTree || treeSha } };
    }
    if (endpoint.startsWith('git/trees/')) {
      const entries = structuredClone(leaves);
      if (selected === newerSha) options.mutate?.(entries);
      return { sha: endpoint.split('/').at(-1).split('?')[0],
        tree: entries, truncated: options.truncated ?? false };
    }
    if (endpoint.startsWith('contents/VERSION?')) return {
      encoding: 'base64', content: Buffer.from(options.version ?? 'v1.2.3\n').toString('base64'),
    };
    throw new Error(`Unexpected qualification read: ${endpoint}`);
  };
}

function memoryStore(initial = state()) {
  let ledger = structuredClone(initial);
  let revision = 0;
  let writes = 0;
  return {
    async read() { return { revision, state: structuredClone(ledger) }; },
    async compareAndSet(expected, next) {
      if (revision !== expected) return false;
      ledger = structuredClone(next);
      revision++;
      writes++;
      return true;
    },
    get writes() { return writes; },
  };
}

test('release API client permits only canonical repository routes and rejects redirects', async () => {
  const prefix = 'https://api.github.com/repos/OlyForge3D/PrintFarmer/';
  for (const [endpoint, method] of [
    [`contents/VERSION?ref=${sha}`, 'GET'], [`git/ref/tags/v1.2.3-insider.1`, 'GET'],
    [`git/trees/${sha}?recursive=1`, 'GET'], [`compare/${sha}...${newerSha}`, 'GET'],
    ['rulesets/123', 'GET'], ['rulesets?per_page=100', 'GET'],
    ['environments/release-stable/deployment-branch-policies', 'GET'],
    ['git/refs', 'POST'], ['git/refs/heads/release-ledger', 'PATCH'],
  ]) assert.equal(githubRequestUrl(endpoint, method), `${prefix}${endpoint}`);
  const previous = globalThis.fetch;
  const calls = [];
  globalThis.fetch = async (url, options) => {
    calls.push({ url, options });
    return new Response(undefined, { status: 204 });
  };
  try {
    const api = githubClient('fixture-token');
    for (const endpoint of [
      'https://attacker.invalid/upload', '//attacker.invalid/upload', '../other/repo/git/refs',
      'git/refs/../../releases', 'git/ref/tags/../../main', 'git/ref/tags/%2e%2e%2fmain',
      'git/ref/tags/v01.2.3', 'git/ref/tags/ios/v1.0-beta.1', `git/commits/${sha}\n`,
      `contents/VERSION?ref=${sha}&path=private`, `git/trees/${sha}?recursive=2`,
      `git/commits/${sha}#fragment`, `git\\commits\\${sha}`, 'git/ref/heads/feature',
      'rulesets/0', 'rulesets/01', 'rulesets?per_page=100&page=2', 'releases',
      'environments/release-publisher-stable',
      'environments/release-publisher-insider/deployment-branch-policies',
    ]) await assert.rejects(api(endpoint), ReleasePolicyError, endpoint);
    for (const method of ['DELETE', 'PUT', 'PATCH', 'POST', 'GET\r\n']) {
      await assert.rejects(api(`git/commits/${sha}`, method), ReleasePolicyError);
    }
    for (const value of ['../main', `${sha}\n`, `${sha}?ref=main`, sha.slice(1)]) {
      await assert.rejects(readVersion(api, value), ReleasePolicyError);
    }
    for (const value of ['../main', 'v1.2.3?ref=main', 'v1.2.3\n']) {
      await assert.rejects(readTag(api, value), ReleasePolicyError);
    }
    assert.deepEqual(calls, [], 'Rejected input must not reach the network');
    await api(`git/commits/${sha}`);
    assert.equal(calls[0].url, `${prefix}git/commits/${sha}`);
    assert.equal(calls[0].options.redirect, 'error', 'Never follow a response to an unapproved destination');
  } finally {
    globalThis.fetch = previous;
  }
});

test('workflow outputs require a runner-owned regular command file and single-line framing', () => {
  const root = resolve('.artifacts', `output-boundary-${process.pid}`);
  const runner = resolve(root, 'runner');
  const directory = resolve(runner, '_runner_file_commands');
  const filename = 'set_output_00000000-0000-0000-0000-000000000000';
  const target = resolve(directory, filename);
  const sentinel = resolve(root, filename);
  const previous = { output: process.env.GITHUB_OUTPUT, runner: process.env.RUNNER_TEMP };
  mkdirSync(directory, { recursive: true });
  writeFileSync(target, '');
  writeFileSync(sentinel, 'untouched');
  process.env.RUNNER_TEMP = runner;
  process.env.GITHUB_OUTPUT = target;
  try {
    output('source_sha', sha);
    output('labels', 'one=two\\nthree=four');
    assert.equal(readFileSync(target, 'utf8'), `source_sha=${sha}\nlabels=one=two\\nthree=four\n`);
    for (const name of ['bad\nname', 'bad\rname', 'name=value', 'name<<EOF', '../name', '']) {
      assert.throws(() => output(name, 'value'), ReleasePolicyError);
    }
    for (const value of ['line\ninjected=yes', 'line\rinjected=yes', '\r\n']) {
      assert.throws(() => output('value', value), ReleasePolicyError);
    }
    const original = readFileSync(target, 'utf8');
    for (const path of [sentinel, `relative-${filename}`, `${target}\n`, resolve(directory, 'arbitrary.txt'),
      resolve(directory, 'set_output_------------------------------------'),
      `${directory}\\..\\..\\${filename}`]) {
      process.env.GITHUB_OUTPUT = path;
      assert.throws(() => output('value', 'blocked'));
    }
    process.env.GITHUB_OUTPUT = target;
    delete process.env.RUNNER_TEMP;
    assert.throws(() => output('value', 'blocked'), ReleasePolicyError);
    process.env.RUNNER_TEMP = runner;
    assert.equal(readFileSync(target, 'utf8'), original);
    rmSync(target);
    linkSync(sentinel, target);
    assert.throws(() => output('value', 'blocked'), /single-link/);
    assert.equal(readFileSync(sentinel, 'utf8'), 'untouched');
    rmSync(target);
    rmSync(directory, { recursive: true });
    symlinkSync(root, directory, process.platform === 'win32' ? 'junction' : 'dir');
    assert.throws(() => output('value', 'blocked'), /destination/);
    assert.equal(readFileSync(sentinel, 'utf8'), 'untouched');
    delete process.env.GITHUB_OUTPUT;
    assert.doesNotThrow(() => output('value', 'local execution'));
  } finally {
    if (previous.output === undefined) delete process.env.GITHUB_OUTPUT;
    else process.env.GITHUB_OUTPUT = previous.output;
    if (previous.runner === undefined) delete process.env.RUNNER_TEMP;
    else process.env.RUNNER_TEMP = previous.runner;
    rmSync(root, { recursive: true, force: true });
  }
});

test('authorization artifacts reject traversal, linked directories and hardlinked destinations', () => {
  const identity = record();
  const cwd = process.cwd();
  const root = resolve('.artifacts', `artifact-boundary-${process.pid}`);
  const outside = resolve(root, 'outside');
  const workspace = resolve(root, 'workspace');
  mkdirSync(outside, { recursive: true });
  mkdirSync(workspace);
  const sentinel = resolve(outside, 'sentinel.json');
  writeFileSync(sentinel, 'untouched');
  process.chdir(workspace);
  try {
    for (const path of ['../outside/sentinel.json', sentinel, 'release-identity.json']) {
      assert.throws(() => readPrivateJson(path), /Invalid authorization source/);
    }
    symlinkSync(outside, '.artifacts', process.platform === 'win32' ? 'junction' : 'dir');
    assert.throws(() => writeAuthorization(identity), /directory must not be linked/);
    assert.deepEqual(readdirSync(outside), ['sentinel.json']);
    rmSync('.artifacts', { recursive: true });
    mkdirSync('.artifacts');
    symlinkSync(outside, '.artifacts/release-authorization', process.platform === 'win32' ? 'junction' : 'dir');
    assert.throws(() => writeAuthorization(identity), /directory must not be linked/);
    rmSync('.artifacts/release-authorization', { recursive: true });
    mkdirSync('.artifacts/release-authorization');
    linkSync(sentinel, authorizationPath);
    assert.throws(() => writeAuthorization(identity), /single-link/);
    assert.equal(readFileSync(sentinel, 'utf8'), 'untouched');
    rmSync(authorizationPath);
    writeAuthorization(identity);
    assert.equal(readFileSync(authorizationPath, 'utf8'), JSON.stringify(identity));
    writeAuthorization(identity);
    assert.equal(readFileSync(authorizationPath, 'utf8'), JSON.stringify(identity), 'Retries preserve signed bytes');
  } finally {
    process.chdir(cwd);
    rmSync(root, { recursive: true, force: true });
  }
});

test('strict canonical grammar accepts beta/RC and rejects every malformed numeric form', () => {
  for (const tag of ['v0.0.0', 'v1.2.3', 'v1.2.3-insider.1', 'v1.2.3-beta.9', 'v1.2.3-rc.10']) {
    assert.equal(parseTag(tag).channel, tag.includes('-') ? 'insider' : 'stable');
  }
  for (const tag of ['v01.2.3', 'v1.02.3', 'v1.2.03', 'v1.2.3-insider.0', 'v1.2.3-beta.01',
    'v1.2.3-rc.00', '1.2.3', 'v1.2.3 ', ' v1.2.3', 'v1.2.3\n', 'v1.2.3+build',
    'v1.2.3-insider.1.2', 'v1.2.3-dev.1', 'v1.2.3-alpha.1', 'ios/v1.2-beta.1', 'v1.2-beta.1']) {
    assert.throws(() => parseTag(tag), tag);
  }
  assert.equal(parseVersionFile('v1.2.3\r\n'), '1.2.3');
  assert.equal(parseVersionFile('v900719925474099300000.2.3\n'), '900719925474099300000.2.3');
  for (const text of [' v1.2.3\n', 'v1.2.3 \n', 'v1.2.3\n\n', 'v1.2.3-insider.1\n']) {
    assert.throws(() => parseVersionFile(text));
  }
  assert.equal(compareVersions('1.2.3-insider.9', '1.2.3-insider.10'), -1);
  assert.equal(compareVersions('1.2.3-insider.9007199254740992', '1.2.3-insider.9007199254740993'), -1);
  assert.equal(compareVersions('1.2.3-beta.90', '1.2.3-insider.1'), -1);
  assert.equal(compareVersions('1.2.3-insider.90', '1.2.3-rc.1'), -1);
});

test('admission denies untrusted events, caller spoofing, source drift and swapped channels before writes', async () => {
  const store = memoryStore();
  for (const override of [
    { event: 'push' }, { event: 'pull_request' }, { event: 'pull_request_target' },
    { event: 'workflow_call' }, { event: 'repository_dispatch' },
    { repository: 'attacker/PrintFarmer' }, { workflowIdentity: 'forged-caller' },
    { workflowSha: newerSha }, { ref: 'refs/tags/v1.2.3' }, { ref: 'refs/heads/feature/test' },
    { ref: 'refs/heads/release' }, { ref: 'refs/heads/release/v1.2.3' }, { channel: 'stable' },
    { requestedTag: 'v1.2.4-insider.1' }, { buildAttempt: '01' }, { stage: 'dev' },
  ]) {
    await assert.rejects(transact(store, state => reserve(state, admission(override), created)));
  }
  assert.equal(store.writes, 0);
  assert.throws(() => admit(context(), newerSha, 'v1.2.3', '1.2.2'), /HEAD/);
  assert.throws(() => admit(context(), sha, 'v1.2.2', '1.2.2'), /exceed/);
  assert.equal(admission({ event: 'schedule' }).channel, 'insider');
  const stable = context({
    channel: 'stable',
    requestedTag: 'v1.2.3',
  });
  assert.equal(admit(stable, sha, 'v1.2.3').channel, 'stable');
  assert.throws(() => admit({ ...stable, event: 'schedule' }, sha, 'v1.2.3'));
});

test('durable reservation reuses identity across same-run attempts and increases N across runs/bases', async () => {
  const store = memoryStore();
  const first = await transact(store, state => reserve(state, admission(), created).record);
  const retry = await transact(store, state => reserve(state, admission(), 'later').record);
  assert.deepEqual(retry, first);
  const second = await transact(store, state => reserve(state, admission({ buildAttempt: '2' }), created).record);
  assert.deepEqual(second, first);
  await transact(store, state => advance(state, first, completeSet(first), sha, ''));
  const baseBump = { ...admission({ buildId: '43' }), baseVersion: '1.3.0' };
  const migrated = { ...baseBump, workflowIdentity: 'owner-approved-replacement' };
  assert.throws(() => reserve(state(), migrated, created), /workflow/);
  const next = await transact(store, state => reserve(state, baseBump, created).record);
  assert.equal(next.sequence, '2');
  assert.notEqual(allocationKey(baseBump), allocationKey(migrated));
  const { state: persisted } = await store.read();
  const resumedStore = memoryStore(persisted);
  assert.equal((await transact(resumedStore, state => reserve(state, baseBump, created).record)).sequence, '2');
  assert.equal(persisted.reservations[first.allocationKey].record.sequence, '1');
});

test('serialized insider allocation rejects overlapping active reservations and never recycles identities', async () => {
  const store = memoryStore();
  const results = await Promise.allSettled(Array.from({ length: 12 }, (_, index) => transact(store,
    state => reserve(state, admission({ buildId: String(index + 100) }), created).record)));
  const accepted = results.filter(result => result.status === 'fulfilled');
  const rejected = results.filter(result => result.status === 'rejected');
  assert.equal(accepted.length, 1);
  assert.equal(rejected.length, 11);
  assert.ok(rejected.every(result => /Unadvanced insider reservation blocks a new insider allocation/.test(result.reason.message)));
  assert.equal((await store.read()).state.counter, '1');
  const retry = await transact(store, state => reserve(state, admission({ buildId: '100' }), created).record);
  assert.equal(retry.sequence, accepted[0].value.sequence);
  await transact(store, state => advance(state, accepted[0].value, completeSet(accepted[0].value), sha, ''));
  const next = await transact(store, state => reserve(state, admission({ buildId: '999' }), created).record);
  assert.equal(next.sequence, '2');
});

test('owner-approved terminal abandonment projects only safe binding fields and consumes identity and sequence', () => {
  const ledger = state();
  const first = record(ledger);
  const approval = abandonmentAuthorization(first, first.protection, abandonmentApproval(first), Date.parse('2026-09-14T22:45:00.000Z'));
  const terminal = abandon(ledger, first, approval, first.protection);
  assert.deepEqual(Object.keys(terminal).sort(), [
    'allocationKey', 'approvalEnvironment', 'approvalJobId', 'approvalRunAttempt', 'approvalRunId', 'approvalTarget',
    'canonicalVersion', 'channel', 'identitySha256', 'ownerApprovedAt', 'schema', 'sourceCommit',
  ]);
  const projected = publicLedger(ledger);
  const persisted = projected.reservations[first.allocationKey].abandonment;
  assert.deepEqual(persisted, terminal);
  assert.doesNotMatch(JSON.stringify(projected), /protectionDigest|protection|ownerApprovedReviewer|private/i);
  const next = record(ledger, { buildId: '43' });
  assert.equal(next.sequence, '2');
  assert.notEqual(next.canonicalVersion, first.canonicalVersion);
  assert.equal(ledger.identities[first.canonicalVersion], first.allocationKey);
  validateLedger(ledger, anchor);
});

test('abandonment rejects forged bindings, duplicate terminal transitions, and activated reservations', () => {
  const ledger = state();
  const first = record(ledger);
  const approval = abandonmentAuthorization(first, first.protection, abandonmentApproval(first), Date.parse('2026-09-14T22:45:00.000Z'));
  for (const mutate of [
    value => { value.allocationKey = 'f'.repeat(64); },
    value => { value.sourceCommit = newerSha; },
    value => { value.canonicalVersion = '1.2.3-insider.9'; },
    value => { value.channel = 'stable'; },
    value => { value.identitySha256 = 'f'.repeat(64); },
    value => { value.protectionDigest = 'f'.repeat(64); },
  ]) {
    const forged = structuredClone(approval);
    mutate(forged);
    assert.throws(() => abandon(ledger, first, forged, first.protection), /Abandonment authorization/);
  }
  abandon(ledger, first, approval, first.protection);
  assert.throws(() => abandon(ledger, first, approval, first.protection), /(cannot be reactivated or abandoned twice|Terminally abandoned reservation)/);

  const activatedLedger = state();
  const activated = record(activatedLedger);
  advance(activatedLedger, activated, completeSet(activated), sha, '');
  assert.throws(() => abandon(activatedLedger, activated,
    abandonmentAuthorization(activated, activated.protection, abandonmentApproval(activated), Date.parse('2026-09-14T22:45:00.000Z')), activated.protection),
  /(cannot be reactivated or abandoned twice|Terminally abandoned reservation)/);
});

test('pointer advancement rejects a stale expected pointer without another ledger write', async () => {
  const store = memoryStore();
  const first = await transact(store, next => reserve(next, admission(), created).record);
  await transact(store, next => advance(next, first, completeSet(first), sha, ''));
  const writes = store.writes;
  const { state: latest } = await store.read();
  assert.throws(() => advance(latest, first, completeSet(first), sha, 'stale-pointer'),
    /Channel compare-and-set conflict/);
  assert.equal(store.writes, writes);
});

test('every new stable and insider reservation exceeds the historical or current stable floor before persistence', async () => {
  for (const kind of ['historical', 'pointer']) {
    for (const channel of ['stable', 'insider']) {
      for (const baseVersion of ['1.2.3', '1.2.4']) {
        const initial = stableFloorLedger(kind, '1.2.4');
        const selected = channel === 'stable' ? stableAdmission(baseVersion, { buildId: '43' })
          : { ...admission(), baseVersion };
        const store = memoryStore(initial);
        await assert.rejects(transact(store, next => reserve(next, selected, created, undefined, hotfixQualification())),
          /must exceed effective stable floor/, `${kind}: ${channel} ${baseVersion}`);
        assert.equal(store.writes, 0);
        assert.deepEqual((await store.read()).state, initial);
      }
      const initial = stableFloorLedger(kind, '1.2.4');
      const selected = channel === 'stable' ? stableAdmission('1.2.5')
        : { ...admission(), baseVersion: '1.2.5' };
      const store = memoryStore(initial);
      const added = await transact(store, next => reserve(next, selected, created, undefined, hotfixQualification()));
      assert.equal(added.record.baseVersion, '1.2.5');
      assert.equal(store.writes, 1);
    }
  }
});

test('CAS retry rejects an insider admitted before concurrent stable advancement without persisting its reservation', async () => {
  const initial = stableFloorLedger('historical', '1.2.2');
  const stable = reserve(initial, stableAdmission(), created, undefined, hotfixQualification()).record;
  const admitted = admission();
  const committed = memoryStore(initial);
  let attempts = 0;
  const store = {
    read: () => committed.read(),
    async compareAndSet(revision) {
      attempts++;
      assert.equal(attempts, 1, 'No second CAS/write is allowed after the floor changes');
      const latest = (await committed.read()).state;
      advance(latest, stable, completeSet(stable), sha, '');
      await committed.compareAndSet(revision, latest);
      return false;
    },
  };
  await assert.rejects(transact(store, next => reserve(next, admitted, created)), /effective stable floor/);
  const { state: persisted } = await committed.read();
  assert.equal(persisted.pointers.stable.canonicalVersion, '1.2.3');
  assert.equal(persisted.counter, '0');
  assert.equal(persisted.reservations[allocationKey(admitted)], undefined);
  assert.equal(committed.writes, 1, 'Only the competing stable pointer transaction was persisted');
});

test('exact stable and insider reservation retries survive a later stable floor while changed admissions do not', async () => {
  const ledger = stableFloorLedger('historical', '1.2.2');
  const insider = record(ledger);
  const admittedStable = stableAdmission();
  const stable = reserve(ledger, admittedStable, created, undefined, hotfixQualification()).record;
  advance(ledger, stable, completeSet(stable), sha, '');
  const stablePointer = signedReleasePointer(stable, signedManifest(stable, completeSet(stable)));
  const newer = reserve(ledger, stableAdmission('1.2.4', { buildId: '43' }),
    created, undefined, hotfixQualification()).record;
  advance(ledger, newer, completeSet(newer), sha, stablePointer.manifestEnvelopeSha256);
  for (const original of [ledger, publicLedger(ledger)]) {
    const store = memoryStore(original);
    for (const [admitted, identity] of [[admission(), insider], [admittedStable, stable]]) {
      const retried = await transact(store, next => reserve(next, admitted, 'ignored for exact retry'));
      assert.deepEqual(retried, original.reservations[identity.allocationKey]);
    }
    await assert.rejects(transact(store, next => reserve(next, admission({ stage: 'rc' }), created)),
      /changed its admission|effective stable floor/);
    assert.deepEqual((await store.read()).state, original);
  }
});

test('continuity loss, reset and malformed persisted state fail closed', () => {
  assert.throws(() => validateLedger(undefined, anchor), /continuity/);
  const ledger = state();
  record(ledger);
  validateLedger(ledger, anchor);
  assert.throws(() => validateLedger(ledger, newerSha), /anchor/);
  ledger.counter = '0';
  assert.throws(() => validateLedger(ledger, anchor), /continuity/);
  ledger.counter = '01';
  assert.throws(() => validateLedger(ledger, anchor), /counter/);
});

test('beta/ordinary insider/RC share N and enforce nonregressing SemVer stage progression', () => {
  const ledger = state();
  const beta = record(ledger, { stage: 'beta' });
  assert.equal(beta.canonicalVersion, '1.2.3-beta.1');
  advance(ledger, beta, completeSet(beta), sha, '');
  const ordinary = record(ledger, { buildId: '43' });
  assert.equal(ordinary.canonicalVersion, '1.2.3-insider.2');
  advance(ledger, ordinary, completeSet(ordinary), sha, ledger.pointers.insider.manifestEnvelopeSha256);
  const rc = record(ledger, { buildId: '44', stage: 'rc' });
  assert.equal(rc.canonicalVersion, '1.2.3-rc.3');
  advance(ledger, rc, completeSet(rc), sha, ledger.pointers.insider.manifestEnvelopeSha256);
  assert.throws(() => record(ledger, { buildId: '45' }), /Stage regression/);
  assert.equal(record(ledger, { stage: 'rc' }).canonicalVersion, '1.2.3-rc.4',
    'A distinct run allocates the next RC identity');
});

test('annotated and lightweight refs peel exactly; moved, deleted and recreated tags are rejected', async () => {
  const identity = record();
  const object = 'd'.repeat(40);
  for (const tag of [{ object, commit: newerSha }, { object: newerSha, commit: sha }, undefined]) {
    assert.throws(() => verifyTag(identity, object, tag));
  }
  const annotated = await readTag(async endpoint => endpoint.startsWith('git/ref/')
    ? { object: { sha: object, type: 'tag' } } : { object: { sha, type: 'commit' } }, identity.sourceTag);
  verifyTag(identity, object, annotated);
  const lightweight = await readTag(async () => ({ object: { sha, type: 'commit' } }), identity.sourceTag);
  verifyTag(identity, sha, lightweight);
  const missing = await readTag(async () => { throw Object.assign(new Error('missing'), { status: 404 }); }, identity.sourceTag);
  assert.equal(missing, undefined);
});

test('source tag is authorized in durable state before public ref creation and never recreated after deletion', async () => {
  const ledger = state();
  const identity = record(ledger);
  const store = memoryStore(ledger);
  const object = 'd'.repeat(40);
  let tag;
  const api = async (endpoint, method) => {
    if (endpoint.startsWith('git/ref/tags/')) {
      if (!tag) throw Object.assign(new Error('missing'), { status: 404 });
      return { object: { sha: object, type: 'tag' } };
    }
    if (endpoint === 'git/tags' && method === 'POST') return { sha: object };
    if (endpoint === `git/tags/${object}`) return { object: { sha, type: 'commit' } };
    if (endpoint === 'git/refs') {
      assert.equal((await store.read()).state.reservations[identity.allocationKey].tagObject, object);
      tag = true;
      return {};
    }
    throw new Error(endpoint);
  };
  await ensureSourceTag(api, store, identity, transact);
  await ensureSourceTag(api, store, identity, transact);
  tag = false;
  await assert.rejects(ensureSourceTag(api, store, identity, transact), /never recreate/);
});

test('record consumers reject foreign callers while allowing same-run retry attempts', () => {
  const identity = record();
  verifyConsumer(identity, { record: identity }, context());
  assert.doesNotThrow(() => verifyConsumer(identity, { record: identity }, context({ buildAttempt: '2' })));
  for (const override of [{ event: 'push' }, { repository: 'fork/repo' },
    { workflowIdentity: 'caller-forgery' }, { workflowSha: newerSha }]) {
    assert.throws(() => verifyConsumer(identity, { record: identity }, context(override)));
  }
  assert.throws(() => verifyConsumer({ ...identity, sourceCommit: newerSha }, { record: identity }, context()));
  for (const override of [{ repository: 'fork/repo' }, { releaseId: 'stable:1.2.3' },
    { canonicalVersion: '1.2.4-insider.1' }, { stage: 'rc' }, { allocationKey: 'forged' }]) {
    const malformed = { ...identity, ...override };
    assert.throws(() => verifyConsumer(malformed, { record: malformed }, context()), ReleasePolicyError);
  }
});

test('complete-set CAS accepts forward branch movement but rejects version and byte regressions', async () => {
  const ledger = state();
  const old = record(ledger);
  const oldPointer = advance(ledger, old, completeSet(old), sha, '');
  const current = record(ledger, { buildId: '43', eventSha: newerSha, workflowSha: newerSha });
  const set = completeSet(current);
  assert.throws(() => validateCompleteSet(current, { ...set, managedEligible: true }), /managed eligibility/);
  advance(ledger, current, set, newerSha, oldPointer.manifestEnvelopeSha256);
  const previous = structuredClone(ledger.pointers);
  assert.equal(advance(ledger, current, set, 'd'.repeat(40), publicSetHash(set)).setHash, publicSetHash(set));
  assert.throws(() => advance(ledger, old, completeSet(old), 'd'.repeat(40), publicSetHash(set)), /version regression/);
  assert.throws(() => advance(ledger, old, completeSet(old), sha, ''), /compare-and-set/);
  assert.deepEqual(ledger.pointers, previous);
  const missing = completeSet(current);
  delete missing.images['slicer-host'];
  assert.throws(() => validateCompleteSet(current, missing), /component/);
  const mixed = completeSet(current);
  mixed.images.frontend.platforms['linux/arm64'].labels['org.printfarmer.release-id'] = old.releaseId;
  assert.throws(() => validateCompleteSet(current, mixed), /identity label/);
  const noArm = completeSet(current);
  delete noArm.images.api.platforms['linux/arm64'];
  assert.throws(() => validateCompleteSet(current, noArm), /platform/);
  const differentBytes = completeSet(current);
  differentBytes.images.api.digest = `sha256:${'f'.repeat(64)}`;
  assert.throws(() => advance(ledger, current, differentBytes, newerSha, publicSetHash(set)), /different bytes/);
  assert.equal(advance(ledger, current, set, newerSha, publicSetHash(set)).setHash, publicSetHash(set));
});

test('durable pointers are closed, signed-byte-bound, and channel-sequenced', () => {
  const ledger = state();
  const insider = record(ledger);
  const insiderSet = completeSet(insider);
  const insiderSigned = signedManifest(insider, insiderSet);
  advance(ledger, insider, insiderSet, sha, '', insiderSigned);
  const pointer = ledger.pointers.insider;
  assert.deepEqual(Object.keys(pointer).sort(), [
    'allocationKey', 'canonicalVersion', 'channel', 'envelopeSha256', 'identitySha256',
    'manifestEnvelopeSha256', 'manifestSha256', 'releaseId', 'sourceCommit', 'stableSequence',
  ]);
  assert.equal(pointer.channel, 'insider');
  assert.equal(ledger.channelSequences.insider, insider.sequence);

  const alteredSet = completeSet(insider);
  alteredSet.images.api.digest = `sha256:${'f'.repeat(64)}`;
  assert.throws(() => advance(ledger, insider, alteredSet, sha, pointer.manifestEnvelopeSha256),
    /Same identity, different bytes/);
  assert.throws(() => advance(ledger, insider, insiderSet, sha, pointer.manifestEnvelopeSha256, {
    ...insiderSigned, serializedEnvelope: `${insiderSigned.serializedEnvelope} `,
  }), /serialization is not canonical/);

  const tampered = structuredClone(ledger);
  tampered.pointers.insider.manifestSha256 = 'f'.repeat(64);
  assert.throws(() => validateLedger(tampered, anchor), /pointer binding/);
  tampered.pointers.insider = { ...ledger.pointers.insider, channel: 'stable' };
  tampered.pointers.stable = tampered.pointers.insider;
  delete tampered.pointers.insider;
  assert.throws(() => validateLedger(tampered, anchor), /pointer/);

  ledger.qualifications[sha] = hotfixQualification();
  const stable = reserve(ledger, stableAdmission(), created, undefined, hotfixQualification()).record;
  advance(ledger, stable, completeSet(stable), newerSha, '', signedManifest(stable, completeSet(stable)));
  assert.equal(ledger.channelSequences.stable, '1');
  const replay = structuredClone(ledger);
  replay.channelSequences.stable = '0';
  assert.throws(() => validateLedger(replay, anchor), /Stable sequence exceeds durable high-water mark/);
  const conflicting = structuredClone(ledger);
  conflicting.pointers.stable.stableSequence = '2';
  assert.throws(() => validateLedger(conflicting, anchor), /pointer binding|Stable pointer sequence replay/);
});

test('stable-sequence migration is explicit, deterministic, and rejects signed legacy state', () => {
  const legacy = state();
  delete legacy.channelSequences;
  const migrated = migrateLegacyLedger(legacy, anchor);
  assert.deepEqual(migrated.channelSequences, { insider: '0', stable: '0' });
  validateLedger(migrated, anchor);

  const pendingLegacy = state();
  pendingLegacy.qualifications[sha] = hotfixQualification();
  const pending = reserve(pendingLegacy, stableAdmission(), created, undefined, hotfixQualification()).record;
  delete pendingLegacy.channelSequences;
  const migratedPending = migrateLegacyLedger(pendingLegacy, anchor);
  assert.deepEqual(migratedPending.channelSequences, { insider: '0', stable: '0' });
  assert.equal(migratedPending.reservations[pending.allocationKey].stableSequence, '1');
  assert.equal(migratedPending.reservations[pending.allocationKey].record.stableSequence, '1');
  validateLedger(migratedPending, anchor);

  const malformed = structuredClone(legacy);
  malformed.pointers.stable = { allocationKey: 'a'.repeat(64) };
  assert.throws(() => migrateLegacyLedger(malformed, anchor), /owner recovery/);
  const signedInsider = state();
  const insider = record(signedInsider);
  signedInsider.reservations[insider.allocationKey].tagObject = sha;
  delete signedInsider.channelSequences;
  assert.throws(() => migrateLegacyLedger(signedInsider, anchor), /owner recovery/);
  assert.throws(() => migrateLegacyLedger(state(), anchor), /pre-stable-sequence/);
});

test('an unadvanced insider reservation blocks overlapping allocations', async () => {
  const ledger = state();
  const a = record(ledger);
  assert.throws(() => record(ledger, { buildId: '43' }),
    /Unadvanced insider reservation blocks a new insider allocation/);
  advance(ledger, a, completeSet(a), sha, '');
  assert.doesNotThrow(() => record(ledger, { buildId: '43' }));
});

test('emitted public identity excludes private and future fields without altering the authorization record', () => {
  const identity = { ...record(), protection: {
    rulesets: [{ id: 'private-ruleset-id' }],
    environment: { id: 'private-environment-id', reviewers: [{ id: 'private-reviewer-id' }] },
  }, futureAuthorization: { policy: 'private-future-value' },
  rulesetId: 'private-ruleset-id', environmentId: 'private-environment-id',
  reviewerId: 'private-reviewer-id', service: 'private-service', commit: 'private-commit',
  buildTime: 'private-time' };
  const original = JSON.stringify(identity);
  const root = resolve('.artifacts', `public-identity-${process.pid}`);
  try {
    emitBuildIdentity(identity, root);
    const emitted = readFileSync(resolve(root, 'src/Web/ReactApp/public/release-identity.json'), 'utf8');
    assert.deepEqual(JSON.parse(emitted), {
      service: 'frontend', commit: sha, buildTime: created,
      releaseId: identity.releaseId, channel: identity.channel,
      canonicalVersion: identity.canonicalVersion, baseVersion: identity.baseVersion,
      sourceBranch: identity.sourceBranch, sourceTag: identity.sourceTag,
      sourceCommit: identity.sourceCommit, authorizedBranchHead: identity.authorizedBranchHead,
      buildId: identity.buildId, buildAttempt: identity.buildAttempt,
      workflowIdentity: identity.workflowIdentity, stableSequence: identity.stableSequence,
      identitySha256: hash(identity),
    });
    assert.doesNotMatch(emitted, /protection|ruleset|environment|reviewer|futureAuthorization|private-/);
    assert.deepEqual(JSON.parse(readFileSync(resolve(root, 'release-identity.json'), 'utf8')), publicAuthorization(identity));
    assert.equal(JSON.stringify(identity), original);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('every platform uses one identity in assemblies/frontend/OCI, including large N', () => {
  const ledger = state();
  ledger.counter = '9007199254740992';
  const identity = record(ledger);
  const metadata = buildMetadata(identity);
  assert.match(metadata.props, /1\.2\.3-insider\.9007199254740993\+sha\.a{40}/);
  const frontend = JSON.parse(metadata.frontend);
  for (const key of ['releaseId', 'channel', 'canonicalVersion', 'sourceCommit', 'buildId', 'buildAttempt']) {
    assert.equal(frontend[key], identity[key]);
    assert.ok(metadata.props.includes(`Include="${key}" Value="${identity[key]}"`));
  }
  validateCompleteSet(identity, completeSet(identity));
  assert.ok(metadata.labels.includes(`org.opencontainers.image.version=${identity.canonicalVersion}`));
});

test('stable and insider frontend outputs copy only canonical public fields and allocation identity', () => {
  const ledger = state();
  ledger.qualifications[sha] = hotfixQualification();
  const stable = reserve(ledger, stableAdmission(), created, undefined, hotfixQualification()).record;
  for (const identity of [stable, record()]) {
    const input = { ...identity, futurePrivate: { value: 'private-value' } };
    const metadata = buildMetadata(input);
    assert.deepEqual(JSON.parse(metadata.frontendIdentity), {
      ...Object.fromEntries(publicIdentityFields.map(field => [field, identity[field]])),
      allocationIdentity: identity.allocationKey,
    });
    assert.doesNotMatch(metadata.frontendIdentity, /protection|qualification|futurePrivate|private-value|identitySha256|buildTime/);
    for (const allocationKey of [undefined, '', 'not-an-allocation', 'a'.repeat(64) + '\n']) {
      assert.throws(() => buildMetadata({ ...identity, allocationKey }), /Invalid frontend allocation/);
    }
  }
});

test('immutable image publication checks all conflicts before any tag writes', () => {
  const identity = record();
  const set = completeSet(identity);
  const tags = new Map();
  const writes = [];
  const create = (tag, source) => { writes.push(tag); tags.set(tag, source.split('@')[1]); };
  publishImmutableTags(identity, set, tag => tags.get(tag), create);
  assert.equal(writes.length, 6);
  publishImmutableTags(identity, set, tag => tags.get(tag), create);
  assert.equal(writes.length, 6);
  assert.ok(writes.every(tag => tag.endsWith(`:${identity.canonicalVersion}`)));
  tags.set(writes[5], `sha256:${'f'.repeat(64)}`);
  assert.throws(() => publishImmutableTags(identity, set, tag => tags.get(tag), create), /conflict/);
  assert.equal(writes.length, 6);
});

test('stable aliases advance only to a strictly newer stable record while insider stays isolated', () => {
  const stableLedger = state();
  stableLedger.qualifications[sha] = hotfixQualification();
  const stable = reserve(stableLedger, stableAdmission(), created, undefined, hotfixQualification()).record;
  const stableSet = completeSet(stable);
  const tags = new Map();
  const writes = [];
  const inspect = tag => tags.get(tag);
  const create = (tag, digest) => {
    writes.push(tag);
    tags.set(tag, { digest: digest.includes('@') ? digest.split('@')[1] : digest, version: stable.canonicalVersion });
  };
  const plan = publishReleaseAliases(stable, stableSet, inspect, create);
  assert.deepEqual(plan.api.map(value => value.tag.split(':').at(-1)),
    ['1.2.3', 'stable-1.2.3', '1.2', '1', 'latest']);
  assert.equal(writes.length, Object.keys(components).length * 5);
  const oldLatest = 'ghcr.io/olyforge3d/printfarmer-api:latest';
  tags.set(oldLatest, { digest: `sha256:${'f'.repeat(64)}`, version: '1.2.4' });
  const before = writes.length;
  publishReleaseAliases(stable, stableSet, inspect, create);
  assert.equal(writes.length, before, 'Newer stable aliases remain untouched');

  const insider = record();
  const insiderPlan = plannedReleaseAliases(insider, completeSet(insider));
  assert.ok(Object.values(insiderPlan).flat().every(value =>
    value.tag.endsWith(`:${insider.canonicalVersion}`) && value.mutable === false));
  assert.throws(() => publishReleaseAliases(insider, completeSet(insider),
    () => ({ digest: `sha256:${'e'.repeat(64)}`, version: '1.2.2' }),
    () => assert.fail('Insider must not overwrite an immutable tag')), /Immutable image tag conflict/);
});

test('alias registry parser accepts realistic multi-platform and single-platform Buildx JSON', () => {
  const digest = `sha256:${'d'.repeat(64)}`;
  const labels = { 'org.opencontainers.image.version': '1.2.3' };
  for (const image of [
    { name: 'ghcr.io/olyforge3d/printfarmer-api:latest', manifest: { digest, mediaType: 'application/vnd.oci.image.index.v1+json' },
      image: { 'linux/amd64': { config: { Labels: labels } }, 'linux/arm64': { config: { Labels: labels } } } },
    { name: 'ghcr.io/olyforge3d/printfarmer-orcaslicer-worker:1.2.3', manifest: { digest },
      image: { config: { Labels: labels }, architecture: 'amd64' } },
  ]) assert.deepEqual(registryTagInspection(JSON.stringify(image)), { digest, version: '1.2.3' });
  assert.throws(() => registryTagInspection(JSON.stringify({
    manifest: { digest }, image: { config: { Labels: { 'org.opencontainers.image.version': '1.2' } } },
  })), /canonical version label/);
});

test('an older stable line retains its scoped aliases without moving newer global aliases', () => {
  const ledger = state();
  ledger.qualifications[sha] = hotfixQualification();
  const stable = reserve(ledger, stableAdmission('1.2.3'), created, undefined, hotfixQualification()).record;
  const set = completeSet(stable);
  const tags = new Map(Object.keys(components).flatMap(component => [
    [`ghcr.io/olyforge3d/printfarmer-${component}:1`, { digest: `sha256:${'e'.repeat(64)}`, version: '1.3.0' }],
    [`ghcr.io/olyforge3d/printfarmer-${component}:latest`, { digest: `sha256:${'e'.repeat(64)}`, version: '1.3.0' }],
  ]));
  const writes = [];
  const plan = publishReleaseAliases(stable, set, tag => tags.get(tag), (tag, digest) => {
    writes.push([tag, digest]);
    tags.set(tag, { digest, version: stable.canonicalVersion });
  });
  assert.deepEqual(plan.api.map(item => item.tag.split(':').at(-1)), ['1.2.3', 'stable-1.2.3', '1.2']);
  assert.ok(writes.every(([tag]) => !tag.endsWith(':1') && !tag.endsWith(':latest')));
});

test('registry inspection executes complete platform/provenance checks rather than accepting flags', () => {
  const identity = record();
  const digest = `sha256:${'d'.repeat(64)}`;
  const inspect = (_command, args) => args.includes('--raw') ? JSON.stringify({
    manifests: (args.some(arg => arg.includes('orcaslicer-worker')) ? ['amd64'] : ['amd64', 'arm64'])
      .flatMap(architecture => [
      { digest, platform: { os: 'linux', architecture } },
      { digest, annotations: { 'vnd.docker.reference.type': 'attestation-manifest',
        'vnd.docker.reference.digest': digest } },
    ]),
  }) : JSON.stringify({ config: { Labels: identityLabels(identity) } });
  const digests = Object.fromEntries(Object.keys(components).map(name => [name, digest]));
  assert.equal(Object.keys(inspectCompleteSet(identity, digests, inspect).images).length, 6);
  assert.throws(() => inspectCompleteSet(identity, digests,
    () => JSON.stringify({ manifests: [] })), /platform/);
});

test('stable promotion requires exact-main qualification and never reuses insider bytes', async () => {
  const ledger = state();
  const insider = record(ledger);
  const set = completeSet(insider);
  advance(ledger, insider, set, sha, '');
  const stable = admission({ channel: 'stable', eventSha: newerSha, workflowSha: newerSha });
  assert.throws(() => reserve(ledger, stable, created), /qualification/);
  ledger.qualifications[newerSha] = promotionQualification(insider, set);
  for (const [field, value] of Object.entries({
    allocationKey: 'd'.repeat(64), releaseId: 'insider:1.2.3-insider.99',
    sourceCommit: newerSha, manifestSha256: 'd'.repeat(64), envelopeSha256: 'd'.repeat(64),
  })) {
    const changed = structuredClone(ledger);
    changed.qualifications[newerSha].promotionOrigin[field] = value;
    assert.throws(() => reserve(publicLedger(changed), stable, created), /qualified immutable insider pointer/);
  }
  assert.throws(() => reserve(ledger, stable, created), /verified at authorization/);
  Object.assign(ledger, publicLedger(ledger));
  const qualification = await verifyStableQualification(promotionApi(), ledger, stable);
  const stableRecord = reserve(ledger, stable, created, undefined, qualification).record;
  assert.deepEqual(stableRecord.qualification, ledger.qualifications[newerSha]);
  assert.throws(() => validateCompleteSet(stableRecord, set), /identity/);
  advance(ledger, stableRecord, completeSet(stableRecord), newerSha, '');
  assert.equal(ledger.pointers.stable.canonicalVersion, '1.2.3');
  assert.equal(ledger.pointers.insider.canonicalVersion, insider.canonicalVersion);
});

test('promotion authorization reproduces full Git trees and rejects unqualified changes, truncation and retagging', async () => {
  const ledger = state();
  const insider = record(ledger);
  const set = completeSet(insider);
  advance(ledger, insider, set, sha, '');
  ledger.qualifications[newerSha] = promotionQualification(insider, set);
  const stable = admission({ channel: 'stable', eventSha: newerSha, workflowSha: newerSha });
  for (const options of [
    { mutate: entries => { entries[1].sha = '1'.repeat(40); } },
    { mutate: entries => { entries[1].mode = '100755'; } },
    { mutate: entries => { entries.pop(); } },
    { mutate: entries => { entries.push({ path: '.github/workflows/publish.yml', type: 'blob',
      mode: '100644', sha: '1'.repeat(40) }); } },
    { mutate: entries => { entries.push({ path: 'empty-directory', type: 'tree',
      mode: '040000', sha: '1'.repeat(40) }); } },
    { mutate: entries => { entries.push(JSON.parse('null')); } },
    { mutate: entries => { entries[0].mode = '120000'; } },
    { mutate: entries => { entries.push(entries[0]); } },
    { truncated: true }, { sourceTree: '1'.repeat(40) }, { version: 'v1.2.4\n' },
  ]) {
    await assert.rejects(verifyStableQualification(promotionApi(options), ledger, stable), ReleasePolicyError);
  }
  const retag = structuredClone(ledger);
  delete retag.qualifications[newerSha];
  retag.qualifications[sha] = promotionQualification(insider, set, sha);
  await assert.rejects(verifyStableQualification(promotionApi(), retag, { ...stable, sourceCommit: sha }),
    /distinct resulting main/);
  const metadata = structuredClone(ledger);
  const evidence = { schema: 1, originTree: 'd'.repeat(40), sourceTree: '1'.repeat(40),
    metadataChanges: [{ path: 'VERSION', before: 'e'.repeat(40), after: '2'.repeat(40) }] };
  metadata.qualifications[newerSha].treeEvidence = { ...evidence, diffSha256: hash(evidence) };
  const api = promotionApi({ sourceTree: '1'.repeat(40), mutate: entries => { entries[0].sha = '2'.repeat(40); } });
  const qualification = await verifyStableQualification(api, metadata, stable);
  const identity = reserve(metadata, stable, created, undefined, qualification).record;
  assert.deepEqual(identity.qualification.treeEvidence, metadata.qualifications[newerSha].treeEvidence);
  assert.throws(() => validateCompleteSet(identity, set), /identity/);
});

test('hotfix qualification requires a normalized non-secret rationale digest, never a boolean', async () => {
  assert.equal(hotfixReasonDigest('  Emergency fix cannot wait   for insider validation. '),
    hotfixReasonDigest('Emergency fix cannot wait for insider validation.'));
  for (const invalid of [undefined, true, '', 'hotfix', 'reason\rspoof', 'reason\nspoof']) {
    assert.throws(() => hotfixReasonDigest(invalid), ReleasePolicyError);
  }
  const stable = admission({ channel: 'stable' });
  for (const reasonSha256 of [undefined, true, 'approved', '', 'f'.repeat(63), `${'f'.repeat(64)}\r`]) {
    const ledger = state();
    ledger.qualifications[sha] = { ...hotfixQualification(), reasonSha256 };
    await assert.rejects(verifyStableQualification(promotionApi(), ledger, stable), ReleasePolicyError);
  }
  const legacy = state();
  const { reasonSha256, ...qualification } = hotfixQualification();
  legacy.qualifications[sha] = { ...qualification, nonPromotionApproved: true };
  await assert.rejects(verifyStableQualification(promotionApi(), legacy, stable), ReleasePolicyError);
});

function nestedSchemaPoisons(object, path = []) {
  const mutations = [];
  const at = value => path.reduce((item, key) => item[key], value);
  if (!object || typeof object !== 'object' || Array.isArray(object)) return mutations;
  mutations.push(value => { at(value).unknownPrivate = { private: 'private-value' }; });
  for (const [key, child] of Object.entries(object)) {
    mutations.push(value => { delete at(value)[key]; });
    for (const invalid of [undefined, JSON.parse('null'), [], {}, '', true, 1]) {
      if (JSON.stringify(child) === JSON.stringify(invalid)) continue;
      mutations.push(value => { at(value)[key] = invalid; });
    }
    if (typeof child === 'string') {
      mutations.push(value => { at(value)[key] = `${child}\r`; });
      mutations.push(value => { at(value)[key] = `${child}\n`; });
    }
    if (Array.isArray(child)) {
      child.forEach((entry, index) => mutations.push(...nestedSchemaPoisons(entry, [...path, key, index])));
    } else mutations.push(...nestedSchemaPoisons(child, [...path, key]));
  }
  return mutations;
}

test('all private/projected reservation variants reject every required nested deletion, alteration and unknown field before writes', async t => {
  const insiderState = state();
  const insider = record(insiderState);
  const insiderSet = completeSet(insider);
  advance(insiderState, insider, insiderSet, sha, '');
  const hotfixState = state();
  hotfixState.qualifications[sha] = hotfixQualification();
  const stableAdmission = admission({ channel: 'stable' });
  const hotfix = reserve(hotfixState, stableAdmission, created, undefined, hotfixQualification()).record;
  const promotionState = structuredClone(insiderState);
  promotionState.qualifications[newerSha] = promotionQualification(insider, insiderSet);
  const promotedAdmission = { ...stableAdmission, sourceCommit: newerSha, authorizedBranchHead: newerSha, workflowCommit: newerSha };
  const promotion = reserve(promotionState, promotedAdmission, created, undefined,
    await verifyStableQualification(promotionApi(), promotionState, promotedAdmission)).record;
  let rejected = 0;
  for (const [original, identity] of [[insiderState, insider], [hotfixState, hotfix], [promotionState, promotion]]) {
    for (const initial of [original, publicLedger(original)]) {
      for (const stage of ['reserved', 'tagObject', 'tagPublished', 'completeSet']) {
        const ledger = structuredClone(initial);
        const entry = ledger.reservations[identity.allocationKey];
        if (stage !== 'completeSet') {
          delete entry.set;
          delete entry.setHash;
          delete ledger.pointers[identity.channel];
        }
        if (stage !== 'reserved') entry.tagObject = 'e'.repeat(40);
        if (['tagPublished', 'completeSet'].includes(stage)) entry.tagPublished = true;
        const poisons = [];
        const required = ['admission', 'record', ...(entry.identitySha256 ? ['identitySha256'] : []),
          ...(entry.sequence ? ['sequence'] : [])];
        poisons.push(value => { value.unknownPrivate = true; });
        for (const field of required) {
          poisons.push(value => { delete value[field]; });
          for (const invalid of [undefined, JSON.parse('null'), [], {}, true, 1, '']) {
            poisons.push(value => { value[field] = invalid; });
          }
        }
        for (const field of ['admission', 'record']) {
          poisons.push(...nestedSchemaPoisons(entry[field]).map(mutate => value => mutate(value[field])));
        }
        for (const poison of poisons) {
          const contaminated = structuredClone(ledger);
          poison(contaminated.reservations[identity.allocationKey]);
          let writes = 0;
          const api = async (_endpoint, method = 'GET') => {
            if (method !== 'GET') writes++;
            throw new Error('Unexpected Git API call');
          };
          const store = {
            read: async () => ({ revision: sha, state: structuredClone(contaminated) }),
            compareAndSet: gitLedger(api, anchor).compareAndSet,
          };
          assert.throws(() => publicLedger(contaminated), ReleasePolicyError);
          await assert.rejects(store.compareAndSet(sha, contaminated), ReleasePolicyError);
          await assert.rejects(ensureSourceTag(api, store, identity, transact), ReleasePolicyError);
          assert.equal(writes, 0);
          rejected++;
        }
      }
    }
  }
  t.diagnostic(`${rejected} nested schema mutations reject projection, CAS and source-tag writes with policy errors`);
});

test('source tagging preflights unrelated qualification, set, reservation and pointer poison before POST git/tags', async () => {
  const initial = state();
  const previous = record(initial);
  advance(initial, previous, completeSet(previous), sha, '');
  const identity = record(initial, { buildId: '43' });
  initial.qualifications[sha] = hotfixQualification();
  const clean = publicLedger(initial);
  for (const mutate of [
    value => { value.qualifications[sha].reviewers = [{ login: 'private-value' }]; },
    value => { value.reservations[previous.allocationKey].set.images.api.digest = 'bad'; },
    value => { delete value.reservations[previous.allocationKey].admission; },
    value => { value.pointers.insider.setHash = 'f'.repeat(64); },
    value => { value.reservations[previous.allocationKey].setHash = 'f'.repeat(64); },
  ]) {
    for (const tagReserved of [false, true]) {
      const ledger = structuredClone(clean);
      if (tagReserved) ledger.reservations[identity.allocationKey].tagObject = 'e'.repeat(40);
      mutate(ledger);
      const writes = [];
      const api = async (endpoint, method = 'GET') => {
        if (method !== 'GET') writes.push({ endpoint, method });
        throw Object.assign(new Error('missing'), { status: 404 });
      };
      const store = { read: async () => ({ revision: sha, state: ledger }),
        compareAndSet: gitLedger(api, anchor).compareAndSet };
      await assert.rejects(ensureSourceTag(api, store, identity, transact), ReleasePolicyError);
      assert.deepEqual(writes, []);
    }
  }
});

test('workflow output rejects CR, LF and CRLF before writing output bytes', () => {
  for (const value of ['a\rb', 'a\nb', 'a\r\nb']) assert.throws(() => output('value', value), ReleasePolicyError);
});

test('well-typed record and admission substitutions cannot break canonical or hash bindings', () => {
  const ledger = state();
  const identity = record(ledger);
  const set = completeSet(identity);
  advance(ledger, identity, set, sha, '');
  for (const variant of [ledger, publicLedger(ledger)]) {
    const overrides = {
      repository: 'other/repository', channel: 'stable', baseVersion: '1.2.4', sourceBranch: 'main',
      sourceCommit: newerSha, authorizedBranchHead: newerSha, workflowCommit: newerSha,
      buildId: '43', buildAttempt: '2',
      workflowIdentity: 'OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/main',
      releaseId: 'insider:1.2.4-insider.1', canonicalVersion: '1.2.4-insider.1',
      sourceTag: 'v1.2.4-insider.1', stage: 'rc', sequence: '2', allocationKey: 'f'.repeat(64),
      created: '2026-09-12T19:00:00.000Z',
      ...(variant.reservations[identity.allocationKey].identitySha256
        ? { identitySha256: 'f'.repeat(64), buildTime: '2026-09-12T21:00:00.000Z' } : {}),
    };
    for (const [field, value] of Object.entries(overrides)) {
      const changed = structuredClone(variant);
      changed.reservations[identity.allocationKey].record[field] = value;
      assert.throws(() => publicLedger(changed), ReleasePolicyError, field);
      if (Object.hasOwn(variant.reservations[identity.allocationKey].admission, field)) {
        const changedAdmission = structuredClone(variant);
        changedAdmission.reservations[identity.allocationKey].admission[field] = value;
        assert.throws(() => publicLedger(changedAdmission), ReleasePolicyError, `admission.${field}`);
      }
    }
  }
  const projected = publicLedger(ledger);
  const entry = projected.reservations[identity.allocationKey];
  assert.equal(entry.setHash, hash(entry.set), 'Anyone can reproduce the hash from the public set alone');
  assert.notEqual(entry.setHash, hash(set), 'The public set hash must not claim to cover hidden authorization fields');
  for (const field of ['releaseId', 'canonicalVersion', 'sourceCommit', 'allocationKey',
    'manifestSha256', 'envelopeSha256', 'manifestEnvelopeSha256']) {
    const changed = structuredClone(projected);
    delete changed.pointers.insider[field];
    assert.throws(() => publicLedger(changed), ReleasePolicyError);
  }
  const stableLedger = state();
  stableLedger.qualifications[sha] = hotfixQualification();
  const stableAdmission = admission({ channel: 'stable' });
  const stable = reserve(stableLedger, stableAdmission, created, undefined, hotfixQualification()).record;
  validateRecord(stable);
  for (const field of ['stage', 'sequence']) {
    for (const value of [undefined, 'insider', '1']) {
      assert.throws(() => validateRecord({ ...stable, [field]: value }), ReleasePolicyError);
    }
  }
});

test('candidate lifecycle rejects expiry, direct publication, deletion without merge-back and version regression', () => {
  const candidateCreated = '2026-09-14T19:00:00.000Z';
  const candidate = { branch: 'release/v1.2.3', target: '1.2.3', sourceCommit: sha, owner: 'maintainer',
    qualification: 'reviewed-commit', created: candidateCreated, expires: '2026-09-14T20:00:00.000Z' };
  validateCandidate(candidate, candidateCreated, 7);
  assert.throws(() => validateCandidate(candidate, '2026-09-15T00:00:00Z', 7), /Expired/);
  assert.throws(() => validateCandidate({ ...candidate, publish: true }, candidateCreated, 7), /never publish/);
  assert.throws(() => validateCandidate({ ...candidate, action: 'delete' }, candidateCreated, 7), /merge-back/);
  validateCandidate({ ...candidate, action: 'delete', abandonmentReason: 'superseded',
    mergeBack: { development: true, activeCandidates: true, versionDidNotRegress: true } }, candidateCreated, 7);
});

test('missing live protections fail closed before a publisher operation', async () => {
  const calls = [];
  await assert.rejects(verifyProtection(async (endpoint, method = 'GET') => {
    calls.push({ endpoint, method }); return [];
  }, 'insider', '123', 'separation-of-duties'), /Owner blocker/);
  assert.ok(calls.length > 0);
  assert.ok(calls.every(call => call.method === 'GET'));
});

test('GitHub ledger adapter uses non-force single-parent CAS and retries only real contention', async () => {
  const calls = [];
  const api = async (endpoint, method, body) => {
    calls.push({ endpoint, method, body });
    if (endpoint === `git/commits/${sha}`) return { tree: { sha: anchor } };
    if (endpoint === 'git/blobs' || endpoint === 'git/trees') return { sha: anchor };
    if (endpoint === 'git/commits') return { sha: newerSha };
    if (endpoint.startsWith('git/refs/')) return {};
    throw new Error(endpoint);
  };
  assert.equal(await gitLedger(api, anchor).compareAndSet(sha, state()), true);
  assert.deepEqual(calls.find(call => call.endpoint === 'git/commits').body.parents, [sha]);
  assert.deepEqual(calls.at(-1).body, { sha: newerSha, force: false });
});

test('workflow entry points have no direct tag/manual Docker bypass; iOS namespace is preserved', () => {
  const docker = readFileSync('.github/workflows/docker-publish.yml', 'utf8');
  assert.match(docker, /workflow_call:/);
  assert.doesNotMatch(docker, /^\s{2}(push|workflow_dispatch|schedule):/m);
  assert.match(docker, /uses: actions\/download-artifact@[0-9a-f]{40}\s+# v8/);
  assert.match(docker, /release-control\.mjs consume/);
  assert.doesNotMatch(docker, /^\s+(?:packages|contents): write$/m);
  assert.match(docker, /password: \$\{\{ secrets\.RELEASE_REGISTRY_TOKEN \}\}/);
  assert.match(docker, /Publish and verify public corresponding-source assets\r?\n\s+if: inputs.operation == 'publish'\r?\n\s+working-directory: source\r?\n\s+env:\r?\n\s+GH_TOKEN: \$\{\{ steps\.publisher\.outputs\.token \}\}/);
  for (const step of docker.split(/^\s{6}- /m)) {
    if (!/uses: actions\/(?:upload|download)-artifact@/.test(step)) continue;
    const selector = step.match(/^\s{10}(?:name|pattern): (.+)$/m)?.[1];
    assert.ok(selector?.includes('github.run_id') ||
      selector === 'release-authorization-${{ github.run_attempt }}',
    `Artifact is not bound to this run or attempt: ${selector}`);
  }
  assert.doesNotMatch(docker, /sort -V|promotion-tags\.txt|release-sha-|manual-\{\{sha\}\}/);
  assert.match(readFileSync('.github/workflows/consolidated-release.yml', 'utf8'),
    /uses: \.\/\.github\/workflows\/docker-publish\.yml/);
  const ios = readFileSync('.github/workflows/testflight-beta.yml', 'utf8');
  assert.match(ios, /TAG_NAME="ios\/v/);
  for (const tag of ['ios/v1.0-beta.106', 'ios/v1.0-rc.1', 'v1.0-beta.106']) {
    assert.throws(() => parseTag(tag));
  }
});

test('single protected publisher requires verified frontend identity for every production build', () => {
  const docker = readFileSync('.github/workflows/docker-publish.yml', 'utf8');
  assert.match(docker, /id: consume[\s\S]*?release-control\.mjs consume/);
  assert.match(docker,
    /PRINTFARMER_RELEASE_IDENTITY: \$\{\{ steps\.consume\.outputs\.frontend_identity \}\}/);
  assert.match(docker, /--build-arg "PRINTFARMER_RELEASE_IDENTITY=\$\{PRINTFARMER_RELEASE_IDENTITY\}"/);
  assert.match(docker, /build_component frontend frontend-runtime linux\/amd64,linux\/arm64/);
  assert.match(docker, /build_component monolith monolith-runtime linux\/amd64,linux\/arm64/);

  const multistage = readFileSync('scripts/docker/dockerfiles/Dockerfile.multistage', 'utf8').replace(/\r\n/g, '\n');
  const stage = multistage.split(' AS frontend-build\n')[1].split('\nFROM ')[0];
  assert.match(stage, /^ARG BUILD_VERSION$/m);
  assert.match(stage, /^ARG PRINTFARMER_RELEASE_IDENTITY$/m);
  assert.match(stage, /^RUN if \[ "\$BUILD_VERSION" != "development" \]; then test -n "\$PRINTFARMER_RELEASE_IDENTITY"; fi$/m);
  assert.ok(stage.indexOf('test -n "$PRINTFARMER_RELEASE_IDENTITY"') < stage.indexOf('npm run build'));
  assert.match(multistage.split(' AS monolith-runtime\n')[1], /COPY --from=frontend-build \/app\/dist \.\/wwwroot\//);
  assert.match(stage, /if \[ "\$build_status" -ne 0 \]; then[\s\S]*?exit \$build_status/);
  assert.equal((multistage.match(/npm run build/g) || []).length, 1, 'New Docker frontend paths need identity coverage');
});

test('Docker frontend guard rejects missing identity before building published versions', () => {
  const multistage = readFileSync('scripts/docker/dockerfiles/Dockerfile.multistage', 'utf8').replace(/\r\n/g, '\n');
  const dockerGuard = multistage.split(' AS frontend-build\n')[1].match(/^RUN (if .*; fi)$/m)?.[1];
  const shell = process.platform === 'win32' ? 'C:\\Program Files\\Git\\bin\\bash.exe' : 'bash';
  const stableLedger = state();
  stableLedger.qualifications[sha] = hotfixQualification();
  const stable = reserve(stableLedger, stableAdmission(), created, undefined, hotfixQualification()).record;
  const insider = reserve(state(), admission(), created).record;
  for (const guard of [dockerGuard]) {
    assert.ok(guard);
    for (const version of ['1.2.3', '1.2.3-insider.42', '']) {
      for (const identity of ['', buildMetadata(
        version.includes('insider') ? insider : stable,
      ).frontendIdentity]) {
        const result = spawnSync(shell, ['-ec', `${guard}\nprintf build-reached`], {
          encoding: 'utf8',
          env: { ...process.env, BUILD_VERSION: version, PRINTFARMER_RELEASE_IDENTITY: identity },
        });
        assert.equal(result.status, identity ? 0 : 1, result.stderr);
        assert.equal(result.stdout, identity ? 'build-reached' : '');
      }
    }
  }
  const local = spawnSync(shell, ['-ec', dockerGuard], {
    encoding: 'utf8', env: { ...process.env, BUILD_VERSION: 'development', PRINTFARMER_RELEASE_IDENTITY: '' },
  });
  assert.equal(local.status, 0, 'Unversioned local Docker builds remain supported');
});

function shellCommands(source) {
  const commands = [];
  let tokens = [];
  let token = '';
  let started = false;
  let quote;
  const flushToken = () => {
    if (started) tokens.push(token);
    token = '';
    started = false;
  };
  const flushCommand = () => {
    flushToken();
    if (tokens.length) commands.push(tokens);
    tokens = [];
  };
  for (let index = 0; index < source.length; index++) {
    const char = source[index];
    if (char === '\n') {
      quote = undefined;
      flushCommand();
    } else if (char === '\\' && quote !== "'" && index + 1 < source.length) {
      token += source[++index];
      started = true;
    } else if (quote) {
      if (char === quote) quote = undefined;
      else token += char;
    } else if (char === '"' || char === "'") {
      quote = char;
      started = true;
    } else if (';&|'.includes(char) || (char === '$' && source[index + 1] === '(')) {
      flushCommand();
      if (char === '$') index++;
    } else if (/\s/.test(char)) {
      flushToken();
    } else {
      token += char;
      started = true;
    }
  }
  flushCommand();
  return commands;
}

function releaseWriteKinds(text) {
  const source = text.replace(/^\s*(?:#|\/\/).*$/gm, '').replace(/\\\r?\n\s*/g, ' ');
  const kinds = [];
  const gitCommands = shellCommands(source).flatMap(tokens => {
    let index = tokens[0] === 'if' ? 1 : 0;
    if (tokens[index++] !== 'git') return [];
    while (['-C', '-c'].includes(tokens[index]) && index + 1 < tokens.length) index += 2;
    return [tokens.slice(index)];
  });
  if (gitCommands.some(([command, ...args]) => command === 'push' && args.some(arg =>
    ['--force', '-f'].includes(arg) || arg.split('=')[0] === '--force-with-lease' || arg.startsWith('+')))) {
    kinds.push('force-push');
  }
  if (gitCommands.some(([command, first]) => command === 'tag' && first !== undefined &&
    !['-l', '-d', '-v', '--list', '--delete', '--verify', '--sort', '--contains', '--points-at']
      .includes(first.split('=')[0])) ||
    /(?:execFile(?:Sync)?|spawn(?:Sync)?|command)\(\s*['"]git['"]\s*,\s*\[\s*['"]tag['"]\s*,\s*(?!['"](?:-l|-d|-v|--list|--delete|--verify|--sort)\b)/.test(source) ||
    /(?:createRef|createTag|create_git_ref|create_git_tag)\s*\(/.test(source) ||
    /['"`]git\/(?:refs|tags)['"`]\s*,\s*['"]POST['"]/.test(source)) {
    kinds.push('tag-write');
  }
  if (/\b(?:gh\s+)?release\s+(?:create|upload|edit)\b/.test(source) ||
    /['"]gh['"]\s*,\s*\[\s*['"]release['"]\s*,\s*['"](?:create|upload|edit)['"]/.test(source) ||
    /(?:createRelease|uploadReleaseAsset|updateRelease|create_release)\s*\(/.test(source) ||
    /uses:\s*(?:softprops\/action-gh-release|ncipollo\/release-action|actions\/(?:create-release|upload-release-asset))@/.test(source)) {
    kinds.push('release-write');
  }
  const publicationEndpoint = /(?:\/releases(?:[/'"`\s]|$)|\/git\/(?:refs|tags))/;
  const publicationVariables = [...source.matchAll(/^\s*(\w+)=([^\n]+)$/gm)]
    .filter(([, , value]) => publicationEndpoint.test(value)).map(([, name]) => name);
  const apiWrites = [...source.matchAll(/\b(?:gh\s+api|curl)\b[^\n]*/g)].map(([call]) => call)
    .filter(call => /(?:(?:--method|-X)\s*['"]?(?:POST|PUT|PATCH)|--data(?:-raw|-binary)?\b|\s-d\b)/.test(call));
  if (apiWrites.some(call => publicationEndpoint.test(call) ||
    publicationVariables.some(name => new RegExp(`\\$\\{?${name}\\b`).test(call)))) {
    kinds.push('publication-api-write');
  }
  return kinds;
}

test('release-writer scanner detects literal, variable, multiline, force-ref and API bypass forms', () => {
  for (const source of [
    'git tag v1.2.3', 'git tag "$NEW_VERSION"', 'git -C "$ROOT" tag -a "$TAG" -m release',
    'git -c "custom.value=;|&" -C "a b" tag "$TAG"', 'if git -C "" tag "$TAG"; then true; fi',
    '$(git -C "$ROOT" tag "$TAG")', 'git -C a\\ b push origin --force',
    'git tag \\\n "$VERSION"', 'git tag -f "$MARKER"',
    'execFileSync("git", ["tag", version])',
    'git push origin main --force', 'git push --force-with-lease origin main',
    'git push origin -f main', 'git -C "$ROOT" push origin "+HEAD:main"',
    'gh release create "$VERSION"', 'gh release upload "$VERSION" asset.zip',
    'create_args=(\n release create "$VERSION"\n --draft\n)\ngh "${create_args[@]}"',
    'gh release \\\n edit "$VERSION" --draft=false',
    'command("gh", ["release", "create", version])',
    'await api("git/refs", "POST", { ref: tag })',
    'await octokit.rest.git.createRef({ ref: tag })',
    'await octokit.rest.repos.createRelease({ tag_name: version })',
    'gh api --method POST "repos/$REPO/releases"',
    'curl -X POST \\\n "https://api.github.com/repos/$REPO/git/refs" -d "$body"',
    'API_URL="https://api.github.com/repos/$REPO/releases"\ncurl -X POST "$API_URL" -d "$body"',
    'uses: softprops/action-gh-release@v3',
  ]) assert.ok(releaseWriteKinds(source).length, source);
  for (const source of [
    'git tag -l "v*"', 'git tag --sort=-version:refname', 'git tag -d "$TAG"',
    'git push origin feature', 'git fetch --tags --force',
    'gh release view "$VERSION"', 'curl -s "https://api.github.com/repos/$REPO/releases"',
    'curl -s "https://api.github.com/repos/$REPO/releases"\ncurl -X PUT "https://api.github.com/orgs/$OWNER/packages/container/$NAME/visibility"',
    '# git tag "$VERSION"\n// gh release create version',
  ]) assert.deepEqual(releaseWriteKinds(source), [], source);
});

test('release-writer scanner handles long option prefixes without backtracking', () => {
  const prefix = `&git ${'-C "" -c \'a=b\' '.repeat(20_000)}`;
  assert.deepEqual(releaseWriteKinds(`${prefix}status`), []);
  assert.deepEqual(releaseWriteKinds(`${prefix}tag --list`), []);
  assert.deepEqual(releaseWriteKinds(`${prefix}tag -a "$TAG" -m release`), ['tag-write']);
  assert.deepEqual(releaseWriteKinds(`${prefix}push origin "+HEAD:main"`), ['force-push']);
  assert.deepEqual(releaseWriteKinds(`${prefix}push --force-with-lease=main:abc origin main`), ['force-push']);
});

test('all executable scripts, actions and workflows have only the reviewed release writers and no force pushes', t => {
  const gitOptions = { encoding: 'utf8', maxBuffer: 16 * 1024 * 1024 };
  const executables = new Set(execFileSync('git', ['ls-files', '--stage', '-z'], gitOptions)
    .split('\0').filter(entry => entry.startsWith('100755 ')).map(entry => entry.split('\t')[1]));
  const files = execFileSync('git', ['ls-files', '--cached', '--others', '--exclude-standard', '-z'],
    gitOptions).split('\0').filter(file =>
    existsSync(file) &&
    (executables.has(file) || /\.(?:sh|bash|ps1|py|rb|js|mjs|cjs)$/.test(file) || /(?:^|\/)Fastfile$/.test(file) ||
      /^(?:scripts|tools|mobile\/scripts|\.github\/scripts)\/(?:[^/]+\/)*[^/.]+$/.test(file) ||
      /^\.github\/.*\.ya?ml$/.test(file) || /^(?:scripts|tools|\.github|\.squad)\/.*\.ts$/.test(file)) &&
    !/(?:^|\/)(?:tests?|__tests__|e2e|node_modules)(?:\/|$)|(?:^|\/)test-[^/]+$|\.(?:test|spec)\./.test(file));
  const approved = {
    'scripts/ci/release-github.mjs': ['tag-write'],
    '.github/workflows/docker-publish.yml': ['release-write'],
    '.github/workflows/testflight-beta.yml': ['tag-write', 'release-write'],
    'mobile/scripts/release-beta.sh': ['tag-write'],
  };
  const found = {};
  for (const file of files) {
    const kinds = releaseWriteKinds(readFileSync(file, 'utf8'));
    if (kinds.length) {
      assert.deepEqual(kinds, approved[file], `Unreviewed publication writer: ${file}`);
      found[file] = kinds;
    }
  }
  assert.deepEqual(found, approved, 'Review the writer inventory explicitly when entry points change');
  const mobile = readFileSync('mobile/scripts/release-beta.sh', 'utf8');
  assert.deepEqual(mobile.match(/^TAG=.*$/gm), ['TAG="ios/v${BASE_VERSION}-beta.${BETA_NUM}"']);
  assert.deepEqual(mobile.match(/^git tag .*$/gm), ['git tag "$TAG"']);
  const ios = readFileSync('.github/workflows/testflight-beta.yml', 'utf8');
  assert.match(ios, /TAG_NAME="\$REF_NAME"\s+if \[\[ "\$TAG_NAME" =~ \^ios\/v/);
  assert.match(ios, /tag_name: \$\{\{ steps\.version\.outputs\.tag_name \}\}/);
  assert.doesNotMatch(ios, /\bgit tag\s+-(?:f|-force)\b/);
  t.diagnostic(`Scanned ${files.length} executable script/action/workflow sources, including mobile release helpers`);
});

test('retired server release helpers fail closed for every invocation without executing publication commands', () => {
  const root = resolve('.artifacts', `retired-publishers-${process.pid}`);
  const bin = resolve(root, 'bin');
  mkdirSync(bin, { recursive: true });
  for (const command of ['git', 'gh', 'docker', 'curl', 'cosign', 'skopeo', 'oras', 'crane', 'npm', 'node']) {
    writeFileSync(resolve(bin, command), '#!/usr/bin/env bash\nprintf "%s\\n" "$0 $*" >> "$SENTINEL_LOG"\nexit 99\n',
      { mode: 0o755 });
  }
  const env = { ...process.env, PATH: `${bin}${process.platform === 'win32' ? ';' : ':'}${process.env.PATH}`,
    SENTINEL_LOG: resolve(root, 'commands.log') };
  try {
    for (const helper of ['scripts/publish-to-public.sh', 'scripts/release.sh']) {
      assert.doesNotMatch(readFileSync(helper, 'utf8'), /\r/, `${helper} must run with Linux Bash, not only Git Bash`);
      for (const args of [[], ['micro'], ['minor'], ['major'], ['patch'], ['--dry-run'],
        ['micro', '--dry-run'], ['--help'], ['v1.2.3'], ['--force']]) {
        const result = spawnSync('bash', [helper, ...args], { env, encoding: 'utf8' });
        assert.equal(result.status, 2, `${helper} ${args.join(' ')}: ${result.error ?? result.stderr}`);
        assert.match(result.stdout + result.stderr, /consolidated-release\.yml/);
        assert.match(result.stdout + result.stderr, /main \(stable\) or development \(insider\)/);
        assert.equal(existsSync(env.SENTINEL_LOG), false, `${helper} must not invoke external publication tools`);
      }
    }
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('GitHub ledger reads pinned Git blobs and rejects ancestry, rollback, truncation and evidence loss', async () => {
  const previous = state();
  const identity = record(previous);
  previous.reservations[identity.allocationKey].tagObject = 'd'.repeat(40);
  advance(previous, identity, completeSet(identity), sha, '');
  const current = structuredClone(previous);
  record(current, { buildId: '43' });
  const fixture = (next = current, options = {}) => async endpoint => {
    if (endpoint === 'git/ref/heads/release-ledger') return { object: { sha: newerSha } };
    if (endpoint.startsWith('compare/')) return { status: options.ancestry || 'ahead' };
    if (endpoint === `git/commits/${anchor}`) return { tree: { sha: anchor }, parents: [] };
    if (endpoint === `git/commits/${newerSha}`) return { tree: { sha: 'head-tree' }, parents: [{ sha }] };
    if (endpoint === `git/commits/${sha}`) return { tree: { sha: 'previous-tree' }, parents: [{ sha: anchor }] };
    if (endpoint.startsWith('git/trees/')) return {
      truncated: options.truncated || false,
      tree: [{ path: 'state.json', type: 'blob', sha: endpoint.endsWith('head-tree') ? 'head-blob' : 'previous-blob' }],
    };
    if (endpoint.startsWith('git/blobs/')) return {
      encoding: 'base64',
      content: Buffer.from(JSON.stringify(endpoint.endsWith('head-blob') ? next : previous)).toString('base64'),
    };
    throw new Error(`Unexpected endpoint: ${endpoint}`);
  };
  assert.equal((await gitLedger(fixture(), anchor).read()).state.counter, '2');
  await assert.rejects(gitLedger(fixture(current, { ancestry: 'diverged' }), anchor).read(), /ancestry/);
  await assert.rejects(gitLedger(fixture(current, { truncated: true }), anchor).read(), /truncated/);
  const reset = state();
  await assert.rejects(gitLedger(fixture(reset), anchor).read(), /rollback/);
  const erased = structuredClone(current);
  delete erased.reservations[identity.allocationKey].tagObject;
  await assert.rejects(gitLedger(fixture(erased), anchor).read(), /immutable tagObject/);
  const replaced = structuredClone(current);
  replaced.reservations[identity.allocationKey].record.sourceCommit = newerSha;
  await assert.rejects(gitLedger(fixture(replaced), anchor).read(), ReleasePolicyError);
  const lostPointer = structuredClone(current);
  delete lostPointer.pointers.insider;
  await assert.rejects(gitLedger(fixture(lostPointer), anchor).read(), /pointer rollback/);
  const lostStage = structuredClone(current);
  delete lostStage.stages;
  await assert.rejects(gitLedger(fixture(lostStage), anchor).read(), /persisted public ledger/);
  const changedAdmission = structuredClone(current);
  changedAdmission.reservations[identity.allocationKey].admission.buildId = '999';
  await assert.rejects(gitLedger(fixture(changedAdmission), anchor).read(), /admission\/record/);
  const orphan = structuredClone(current);
  orphan.identities['1.2.4-insider.9'] = 'unknown';
  await assert.rejects(gitLedger(fixture(orphan), anchor).read(), /identity reference/);
  // Floors may not fabricate pointers/stages without their immutable reservations.
  previous.reservations = {};
  previous.identities = {};
  const seedNext = structuredClone(previous);
  delete seedNext.pointers.insider;
  await assert.rejects(gitLedger(fixture(seedNext), anchor).read(), ReleasePolicyError);
  seedNext.pointers = structuredClone(previous.pointers);
  delete seedNext.stages;
  await assert.rejects(gitLedger(fixture(seedNext), anchor).read(), ReleasePolicyError);
});

test('GitHub CAS distinguishes a competing head from a rejected protected write', async () => {
  const api = head => async (endpoint, method) => {
    if (endpoint === `git/commits/${sha}`) return { tree: { sha: anchor } };
    if (method === 'POST') return { sha: newerSha };
    if (method === 'PATCH') throw Object.assign(new Error('conflict'), { status: 422 });
    if (endpoint === 'git/ref/heads/release-ledger') return { object: { sha: head } };
    throw new Error(endpoint);
  };
  assert.equal(await gitLedger(api(newerSha), anchor).compareAndSet(sha, state()), false);
  await assert.rejects(gitLedger(api(sha), anchor).compareAndSet(sha, state()), /policy, not a CAS/);
});

function adjacentLedgerFixture(previous, current) {
  const calls = [];
  const api = async (endpoint, method = 'GET') => {
    calls.push({ endpoint, method });
    assert.equal(method, 'GET', 'Invalid history must fail before any Git write');
    if (endpoint === 'git/ref/heads/release-ledger') return { object: { sha: newerSha } };
    if (endpoint.startsWith('compare/')) return { status: 'ahead' };
    if (endpoint === `git/commits/${anchor}`) return { tree: { sha: anchor }, parents: [] };
    if (endpoint === `git/commits/${newerSha}`) return { tree: { sha: 'head-tree' }, parents: [{ sha }] };
    if (endpoint === `git/commits/${sha}`) return { tree: { sha: 'previous-tree' }, parents: [{ sha: anchor }] };
    if (endpoint.startsWith('git/trees/')) return {
      truncated: false,
      tree: [{ path: 'state.json', type: 'blob', sha: endpoint.endsWith('head-tree') ? 'head-blob' : 'previous-blob' }],
    };
    if (endpoint.startsWith('git/blobs/')) return {
      encoding: 'base64',
      content: Buffer.from(JSON.stringify(endpoint.endsWith('head-blob') ? current : previous)).toString('base64'),
    };
    throw new Error(`Unexpected endpoint: ${endpoint}`);
  };
  return { api, calls, store: gitLedger(api, anchor) };
}

function ledgerHistoryFixture(snapshots, mutate = () => {}) {
  const objects = new Map();
  const revisions = snapshots.map((_, index) => (index + 1).toString(16).padStart(40, '0'));
  snapshots.forEach((snapshot, index) => {
    const tree = (snapshots.length + index + 1).toString(16).padStart(40, '0');
    const blob = (snapshots.length * 2 + index + 1).toString(16).padStart(40, '0');
    objects.set(`git/commits/${revisions[index]}`, {
      sha: revisions[index], tree: { sha: tree }, parents: [{ sha: revisions[index - 1] ?? anchor }],
    });
    objects.set(`git/trees/${tree}`, {
      truncated: false, tree: [{ path: 'state.json', type: 'blob', sha: blob }],
    });
    objects.set(`git/blobs/${blob}`, {
      encoding: 'base64', content: Buffer.from(JSON.stringify(snapshot)).toString('base64'),
    });
  });
  objects.set(`git/commits/${anchor}`, { sha: anchor, tree: { sha: 'd'.repeat(40) }, parents: [] });
  objects.set('git/ref/heads/release-ledger', { object: { sha: revisions.at(-1) } });
  objects.set(`compare/${anchor}...${revisions.at(-1)}`, {
    status: 'ahead', total_commits: revisions.length, commits: [{ sha: revisions.at(-1) }],
  });
  mutate(objects, revisions);
  const calls = [];
  const api = async (endpoint, method = 'GET') => {
    calls.push({ endpoint, method });
    assert.equal(method, 'GET', 'Invalid history must fail before any Git write');
    const object = objects.get(endpoint);
    if (!object) throw Object.assign(new Error(`Unresolvable ledger object: ${endpoint}`), { status: 404 });
    return structuredClone(object);
  };
  return { api, calls, revisions, store: gitLedger(api, anchor) };
}

async function assertHistoryRejected(fixtureFactory, identity, expected, label) {
  for (const [operation, run] of [
    ['ledger read', fixture => fixture.store.read()],
    ['allocation transaction', fixture => transact(fixture.store,
      next => reserve(next, admission({ buildAttempt: '3' }), created))],
    ['source tag publication', fixture => ensureSourceTag(fixture.api, fixture.store, identity, transact)],
  ]) {
    const fixture = fixtureFactory();
    await assert.rejects(run(fixture), expected, `${label}: ${operation}`);
    assert.equal(fixture.calls.filter(call => call.method === 'POST' || call.method === 'PATCH').length, 0,
      `${label}: ${operation} must reject before creating blobs, tags, commits or refs`);
  }
}

test('every historical stable floor edge preserves exact presence and value before writes', async () => {
  const seed = state();
  const identity = record(seed);
  for (const [name, floor, mutate] of [
    ['delete floor', '1.2.2', next => { delete next.lastHistoricalStable; }],
    ['lower floor', '1.2.2', next => { next.lastHistoricalStable = '1.2.1'; }],
    ['replace floor', '1.2.2', next => { next.lastHistoricalStable = '1.2.3'; }],
    ['introduce floor after seed', undefined, next => { next.lastHistoricalStable = '1.2.2'; }],
  ]) {
    const previous = publicLedger(seed);
    if (floor !== undefined) previous.lastHistoricalStable = floor;
    const changed = structuredClone(previous);
    mutate(changed);
    validateLedger(previous, anchor);
    validateLedger(changed, anchor);
    for (const [history, snapshots] of [
      ['adjacent', [previous, changed]],
      ['retained at head', [previous, changed, changed]],
      ['restored at head', [previous, changed, previous]],
    ]) {
      await assertHistoryRejected(() => ledgerHistoryFixture(snapshots), identity,
        /immutable historical stable floor/, `${name}, ${history}`);
    }
  }
});

test('history rejects valid-schema new reservations at or below the preceding stable floor before every write', async () => {
  for (const kind of ['historical', 'pointer']) {
    for (const channel of ['stable', 'insider']) {
      for (const baseVersion of ['1.2.2', '1.2.3', ...(kind === 'historical' || channel === 'insider' ? ['1.2.4'] : [])]) {
        const previous = publicLedger(stableFloorLedger(kind, '1.2.4'));
        const isolated = state();
        isolated.counter = previous.counter;
        isolated.channelSequences = structuredClone(previous.channelSequences);
        isolated.qualifications[sha] = hotfixQualification();
        const selected = channel === 'stable' ? stableAdmission(baseVersion, { buildId: '43' })
          : { ...admission(), baseVersion };
        const identity = reserve(isolated, selected, created, undefined, hotfixQualification()).record;
        const added = publicLedger(isolated);
        const changed = structuredClone(previous);
        Object.assign(changed.reservations, added.reservations);
        Object.assign(changed.identities, added.identities);
        Object.assign(changed.stages, added.stages);
        changed.counter = added.counter;
        changed.channelSequences = added.channelSequences;
        validateLedger(changed, anchor);
        for (const snapshots of [[previous, changed], [previous, changed, changed]]) {
          await assertHistoryRejected(() => ledgerHistoryFixture(snapshots), identity,
            /effective stable floor/, `${kind}: ${channel} ${baseVersion}`);
        }
      }
    }
  }
});

test('every counter edge requires exactly one matching insider reservation and no unexplained additions', async () => {
  const seed = state();
  seed.counter = '10';
  const identity = record(seed);
  const previous = publicLedger(seed);
  const addInsider = (next, sequence, buildId = '99', baseVersion = '1.2.3') => {
    const isolated = state();
    isolated.counter = (BigInt(sequence) - 1n).toString();
    const admitted = admit(context({ buildId }), sha, `v${baseVersion}\n`, '1.2.2');
    const added = reserve(isolated, admitted, created).record;
    next.reservations[added.allocationKey] = publicLedger(isolated).reservations[added.allocationKey];
    next.identities[added.canonicalVersion] = added.allocationKey;
    next.channelSequences.insider = (BigInt(next.channelSequences.insider) > BigInt(sequence)
      ? next.channelSequences.insider : sequence);
    if (!next.stages[baseVersion] || compareVersions(added.canonicalVersion, next.stages[baseVersion]) > 0) {
      next.stages[baseVersion] = added.canonicalVersion;
    }
  };
  const addStable = (next, baseVersion = '1.2.3') => {
    next.qualifications[sha] = hotfixQualification();
    const admitted = admit(context({ channel: 'stable' }),
    sha, `v${baseVersion}\n`, '1.2.2');
    if (!Object.values(next.reservations).some(item => item.record.channel === 'stable')) {
      reserve(next, admitted, created, undefined, hotfixQualification());
      return;
    }
    const isolated = state();
    isolated.qualifications[sha] = hotfixQualification();
    const reservation = reserve(isolated, admitted, created, undefined, hotfixQualification());
    next.reservations[reservation.record.allocationKey] = reservation;
    next.identities[reservation.record.canonicalVersion] = reservation.record.allocationKey;
  };
  for (const [name, mutate, structurallyValid = true] of [
    ['unbound increment', next => { next.counter = '12'; }],
    ['unbound jump', next => { next.counter = '13'; }],
    ['unbound large jump', next => { next.counter = '9007199254740993'; }],
    ['jump with skipped sequence', next => { next.counter = '13'; addInsider(next, '13'); }],
    ['jump with two allocations', next => {
      next.counter = '13'; addInsider(next, '12'); addInsider(next, '13', '100');
    }],
    ['increment with wrong sequence', next => { next.counter = '12'; addInsider(next, '5'); }],
    ['unchanged counter with old sequence addition', next => { addInsider(next, '5'); }],
    ['unchanged counter with two old sequence additions', next => {
      addInsider(next, '5'); addInsider(next, '6', '100');
    }],
    ['increment with matching and unexplained allocation', next => {
      next.counter = '12'; addInsider(next, '5'); addInsider(next, '12', '100');
    }],
    ['increment with only stable allocation', next => { next.counter = '12'; addStable(next); }],
    ['increment with insider and stable allocations', next => {
      next.counter = '12'; addInsider(next, '12'); addStable(next);
    }],
    ['unchanged counter with two stable allocations', next => { addStable(next); addStable(next, '1.2.4'); }, false],
    ['duplicate global sequence on another base', next => { addInsider(next, '11', '99', '1.2.4'); }, false],
    ['new sequence above unchanged counter', next => { addInsider(next, '12'); }, false],
  ]) {
    const changed = structuredClone(previous);
    mutate(changed);
    const persisted = structurallyValid ? publicLedger(changed) : changed;
    for (const [history, snapshots] of [
      ['adjacent', [previous, persisted]],
      ['retained at head', [previous, persisted, persisted]],
      ['restored at head', [previous, persisted, previous]],
    ]) {
      await assertHistoryRejected(() => ledgerHistoryFixture(snapshots), identity,
        ReleasePolicyError, `${name}, ${history}`);
    }
  }
});

test('valid every-edge allocations preserve seed floors, stable semantics and non-allocation transactions', async () => {
  for (const floor of [undefined, '1.2.2']) {
    const ledger = state();
    ledger.counter = '9007199254740993';
    if (floor !== undefined) ledger.lastHistoricalStable = floor;
    const snapshots = [publicLedger(ledger)];
    const append = mutate => {
      const result = mutate(ledger);
      snapshots.push(publicLedger(ledger));
      return result;
    };
    const insider = append(next => record(next));
    assert.equal(insider.sequence, '9007199254740994');
    append(next => { next.reservations[insider.allocationKey].tagObject = 'd'.repeat(40); });
    append(next => { next.reservations[insider.allocationKey].tagPublished = true; });
    append(next => advance(next, insider, completeSet(insider), sha, ''));
    append(next => record(next));
    append(next => { next.qualifications[sha] = hotfixQualification(); });
    const stableAdmission = admission({ channel: 'stable' });
    const stable = append(next => reserve(next, stableAdmission, created, undefined, hotfixQualification()).record);
    append(next => reserve(next, stableAdmission, created, undefined, hotfixQualification()));
    append(next => advance(next, stable, completeSet(stable), sha, ''));
    assert.equal(ledger.counter, insider.sequence, 'Stable allocations and pointer updates consume no sequence');
    append(next => reserve(next, admission(), created));
    append(next => reserve(next, stableAdmission, created));
    const rcAdmission = admit(context({ buildId: '43', buildAttempt: '2', stage: 'rc' }), sha, 'v1.2.4\n', '1.2.3');
    const rc = append(next => reserve(next, rcAdmission, created).record);
    assert.equal(rc.sequence, '9007199254740995');
    append(next => advance(next, rc, completeSet(rc), sha, next.pointers.insider.manifestEnvelopeSha256));
    const betaAdmission = admit(context({ buildId: '44', buildAttempt: '3', stage: 'beta' }), sha, 'v1.2.5\n', '1.2.3');
    const beta = append(next => reserve(next, betaAdmission, created).record);
    assert.equal(beta.sequence, '9007199254740996');
    const fixture = ledgerHistoryFixture(snapshots);
    assert.deepEqual((await fixture.store.read()).state, snapshots.at(-1));
    assert.equal(fixture.calls.filter(call => call.endpoint.startsWith('git/commits/')).length, snapshots.length + 1);
    assert.equal(fixture.calls.filter(call => call.method === 'POST' || call.method === 'PATCH').length, 0);
    for (const snapshot of snapshots) {
      assert.equal(Object.hasOwn(snapshot, 'lastHistoricalStable'), floor !== undefined);
      assert.equal(snapshot.lastHistoricalStable, floor);
    }
  }
});

for (const mode of ['promotion', 'hotfix']) {
  for (const consumed of [false, true]) {
    test(`complete history preserves ${consumed ? 'consumed' : 'unconsumed'} ${mode} qualifications before every write`, async () => {
      const seed = state();
      const insider = record(seed);
      const insiderSet = completeSet(insider);
      advance(seed, insider, insiderSet, sha, '');
      const alternative = record(seed, { buildId: '43', buildAttempt: '2',
        eventSha: 'f'.repeat(40), workflowSha: 'f'.repeat(40) });
      const alternativeSet = completeSet(alternative);
      advance(seed, alternative, alternativeSet, alternative.sourceCommit, publicSetHash(insiderSet));
      const promotion = promotionQualification(alternative, alternativeSet);
      const hotfix = { ...hotfixQualification(), sourceCommit: newerSha };
      const qualification = structuredClone(mode === 'promotion' ? promotion : hotfix);
      if (mode === 'promotion') {
        qualification.promotionOrigin = Object.fromEntries(Object.entries(qualification.promotionOrigin).reverse());
        qualification.treeEvidence = Object.fromEntries(Object.entries(qualification.treeEvidence).reverse());
      }
      seed.qualifications[newerSha] = Object.fromEntries(Object.entries(qualification).reverse());
      let identity = insider;
      if (consumed) {
        const stableAdmission = admission({ channel: 'stable',
          eventSha: newerSha, workflowSha: newerSha });
        identity = reserve(seed, stableAdmission, created, undefined, mode === 'promotion' ? promotion : hotfix).record;
      }
      const previous = publicLedger(seed);
      assert.equal(JSON.stringify(previous.qualifications[newerSha]), JSON.stringify(seed.qualifications[newerSha]),
        'Projection must not rewrite valid qualification field order');
      if (consumed) assert.equal(Object.hasOwn(previous.reservations[identity.allocationKey].record, 'qualification'), false,
        'Persisted stable records omit qualification: continuity must bind the separate retained evidence');
      const appended = structuredClone(previous);
      appended.qualifications['e'.repeat(40)] = { ...hotfix, sourceCommit: 'e'.repeat(40) };
      const valid = adjacentLedgerFixture(previous, appended);
      assert.deepEqual((await valid.store.read()).state, appended);
      assert.equal(JSON.stringify(appended.qualifications[newerSha]), JSON.stringify(previous.qualifications[newerSha]));
      assert.equal(valid.calls.filter(call => call.method === 'POST' || call.method === 'PATCH').length, 0);
      const persisted = [];
      const writer = gitLedger(async (endpoint, method, body) => {
        if (endpoint === `git/commits/${sha}`) return { tree: { sha: anchor } };
        if (endpoint === 'git/blobs' && method === 'POST') persisted.push(JSON.parse(body.content));
        return { sha: newerSha };
      }, anchor);
      assert.equal(await writer.compareAndSet(sha, appended), true);
      assert.equal(persisted.length, 1);
      assert.equal(JSON.stringify(persisted[0].qualifications), JSON.stringify(appended.qualifications),
        'Actual serialized Git blob preserves existing and appended qualification bytes');
      assert.deepEqual((await adjacentLedgerFixture(previous, persisted[0]).store.read()).state, appended);
      const appendedAgain = structuredClone(appended);
      appendedAgain.qualifications['d'.repeat(40)] = { ...hotfix, sourceCommit: 'd'.repeat(40) };
      assert.deepEqual((await ledgerHistoryFixture([previous, appended, appendedAgain]).store.read()).state, appendedAgain);

      const mutations = [
        ['delete qualification', next => { delete next.qualifications[newerSha]; }, !consumed],
        ['replace qualification', next => { next.qualifications[newerSha] = mode === 'promotion' ? hotfix : promotion; }, true],
        ['reorder qualification fields', next => {
          next.qualifications[newerSha] = Object.fromEntries(Object.entries(next.qualifications[newerSha]).reverse());
        }, true],
        ...(mode === 'promotion' ? [
          ['delete promotionOrigin', next => { delete next.qualifications[newerSha].promotionOrigin; }, false],
          ['replace promotionOrigin', next => {
            next.qualifications[newerSha].promotionOrigin = promotionQualification(alternative, alternativeSet).promotionOrigin;
          }, true],
          ['delete treeEvidence', next => { delete next.qualifications[newerSha].treeEvidence; }, false],
          ['replace treeEvidence with valid digest', next => {
            const evidence = { schema: 1, originTree: 'e'.repeat(40), sourceTree: 'f'.repeat(40),
              metadataChanges: [{ path: 'VERSION', before: '1'.repeat(40), after: '2'.repeat(40) }] };
            next.qualifications[newerSha].treeEvidence = { ...evidence, diffSha256: hash(evidence) };
          }, true],
        ] : [
          ['delete reasonSha256', next => { delete next.qualifications[newerSha].reasonSha256; }, false],
          ['replace reasonSha256', next => {
            next.qualifications[newerSha].reasonSha256 = hotfixReasonDigest('Different emergency rationale replaces the approved original.');
          }, true],
        ]),
      ];
      for (const [name, mutate, structurallyValid] of mutations) {
        const next = structuredClone(previous);
        mutate(next);
        if (structurallyValid) validateLedger(next, anchor);
        for (const [history, snapshots] of [
          ['adjacent', [previous, next]],
          ['retained at head', [previous, next, next]],
          ['restored at head', [previous, next, previous]],
        ]) {
          await assertHistoryRejected(() => ledgerHistoryFixture(snapshots), identity,
            structurallyValid ? /immutable qualification/ : ReleasePolicyError, `${name}, ${history}`);
        }
      }
    });
  }
}

test('persisted ledger schema rejects unknown top-level fields in either adjacent snapshot before writes', async () => {
  const seed = state();
  const identity = record(seed);
  const clean = publicLedger(seed);
  for (const location of ['parent', 'child']) {
    const previous = structuredClone(clean);
    const next = structuredClone(clean);
    (location === 'parent' ? previous : next).ownerNotes = 'unapproved persisted field';
    for (const run of [
      fixture => fixture.store.read(),
      fixture => transact(fixture.store, ledger => reserve(ledger, admission({ buildAttempt: '2' }), created)),
      fixture => ensureSourceTag(fixture.api, fixture.store, identity, transact),
    ]) {
      const fixture = adjacentLedgerFixture(previous, next);
      await assert.rejects(run(fixture), /persisted public ledger/);
      assert.equal(fixture.calls.filter(call => call.method === 'POST' || call.method === 'PATCH').length, 0);
    }
  }
});

test('every continuity invariant is enforced before writes across older history edges', async () => {
  const seed = state();
  const first = record(seed);
  seed.reservations[first.allocationKey].tagObject = 'd'.repeat(40);
  seed.reservations[first.allocationKey].tagPublished = true;
  advance(seed, first, completeSet(first), sha, '');
  const second = record(seed, { buildId: '43' });
  advance(seed, second, completeSet(second), sha, publicSetHash(completeSet(first)));
  seed.counter = '10';
  const previous = publicLedger(seed);
  const mutations = [
    ['counter rollback', next => { next.counter = '2'; }, /counter rollback/],
    ['reservation deletion', next => {
      delete next.reservations[first.allocationKey];
      delete next.identities[first.canonicalVersion];
    }, /immutable reservation/],
    ['tag object replacement', next => {
      next.reservations[first.allocationKey].tagObject = 'e'.repeat(40);
    }, /immutable tagObject/],
    ['tag publication deletion', next => {
      delete next.reservations[first.allocationKey].tagPublished;
    }, /immutable tagPublished/],
    ['complete set deletion', next => {
      delete next.reservations[first.allocationKey].set;
      delete next.reservations[first.allocationKey].setHash;
    }, /immutable setHash/],
    ['complete set replacement', next => {
      const entry = next.reservations[first.allocationKey];
      entry.set.images.api.digest = `sha256:${'a'.repeat(64)}`;
      entry.setHash = hash(writePublicSet(entry.record, entry.set, entry.identitySha256));
    }, /immutable setHash/],
    ['pointer deletion', next => { delete next.pointers.insider; }, /pointer rollback/],
    ['pointer rollback', next => {
      next.pointers.insider = signedReleasePointer(first,
        signedManifest(first, next.reservations[first.allocationKey].set));
    }, /pointer rollback/],
  ];
  for (const [name, mutate, expected] of mutations) {
    const changed = structuredClone(previous);
    mutate(changed);
    validateLedger(changed, anchor);
    await assertHistoryRejected(() => ledgerHistoryFixture([previous, changed, changed]), second, expected, name);
  }
  for (const [name, mutate] of [
    ['stage rollback', next => { next.stages[first.baseVersion] = first.canonicalVersion; }],
    ['stage deletion', next => { delete next.stages[first.baseVersion]; }],
    ['admission replacement', next => { next.reservations[first.allocationKey].admission.buildId = '999'; }],
    ['record replacement', next => { next.reservations[first.allocationKey].record.sourceCommit = newerSha; }],
  ]) {
    const changed = structuredClone(previous);
    mutate(changed);
    await assertHistoryRejected(() => ledgerHistoryFixture([previous, changed, changed]), second, ReleasePolicyError, name);
  }
});

test('every historical snapshot is closed-schema validated even when the head restores clean state', async () => {
  const seed = state();
  const identity = record(seed);
  const clean = publicLedger(seed);
  for (const index of [0, 1, 2]) {
    for (const [name, mutate] of [
      ['unknown top-level field', next => { next.ownerNotes = 'not a public field'; }],
      ['unknown nested field', next => { next.reservations[identity.allocationKey].admission.ownerNotes = 'invalid'; }],
      ['missing map', next => { delete next.qualifications; }],
      ['missing nested field', next => { delete next.reservations[identity.allocationKey].record.created; }],
    ]) {
      const snapshots = [clean, clean, clean].map(snapshot => structuredClone(snapshot));
      mutate(snapshots[index]);
      await assertHistoryRejected(() => ledgerHistoryFixture(snapshots), identity, ReleasePolicyError, `${name} at ${index}`);
    }
  }
});

test('history rejects merges, cycles, missing objects and truncation despite an ahead comparison', async () => {
  const seed = state();
  const identity = record(seed);
  const clean = publicLedger(seed);
  const mutations = [
    ['intermediate merge', (objects, revisions) => {
      objects.get(`git/commits/${revisions[1]}`).parents.push({ sha: anchor });
    }, /single-parent/],
    ['intermediate octopus', (objects, revisions) => {
      objects.get(`git/commits/${revisions[1]}`).parents.push({ sha: anchor }, { sha: sha });
    }, /single-parent/],
    ['unreachable anchor', (objects, revisions) => {
      objects.get(`git/commits/${revisions[0]}`).parents = [];
    }, /single-parent/],
    ['self cycle', (objects, revisions) => {
      objects.get(`git/commits/${revisions[1]}`).parents = [{ sha: revisions[1] }];
    }, /cycle/],
    ['multi-commit cycle', (objects, revisions) => {
      objects.get(`git/commits/${revisions[0]}`).parents = [{ sha: revisions[2] }];
    }, /cycle/],
    ['missing parent list', (objects, revisions) => {
      delete objects.get(`git/commits/${revisions[1]}`).parents;
    }, /single-parent/],
    ['invalid parent SHA', (objects, revisions) => {
      objects.get(`git/commits/${revisions[1]}`).parents = [{ sha: 'not-a-sha' }];
    }, /ledger parent commit/],
    ['unresolvable boundary', objects => { objects.delete(`git/commits/${anchor}`); }, /Unresolvable/],
    ['malformed boundary', objects => { objects.set(`git/commits/${anchor}`, {}); }, /ledger anchor tree/],
  ];
  for (const index of [0, 1, 2]) {
    mutations.push(
      [`merge at ${index}`, (objects, revisions) => {
        objects.get(`git/commits/${revisions[index]}`).parents.push({ sha: anchor });
      }, /single-parent/],
      [`unresolvable commit at ${index}`, (objects, revisions) => {
        objects.delete(`git/commits/${revisions[index]}`);
      }, /Unresolvable/],
      [`unresolvable tree at ${index}`, (objects, revisions) => {
        objects.delete(`git/trees/${objects.get(`git/commits/${revisions[index]}`).tree.sha}`);
      }, /Unresolvable/],
      [`truncated tree at ${index}`, (objects, revisions) => {
        objects.get(`git/trees/${objects.get(`git/commits/${revisions[index]}`).tree.sha}`).truncated = true;
      }, /truncated/],
      [`missing state at ${index}`, (objects, revisions) => {
        objects.get(`git/trees/${objects.get(`git/commits/${revisions[index]}`).tree.sha}`).tree = [];
      }, /state is missing/],
      [`unresolvable blob at ${index}`, (objects, revisions) => {
        const tree = objects.get(`git/trees/${objects.get(`git/commits/${revisions[index]}`).tree.sha}`);
        objects.delete(`git/blobs/${tree.tree[0].sha}`);
      }, /Unresolvable/],
      [`malformed snapshot at ${index}`, (objects, revisions) => {
        const tree = objects.get(`git/trees/${objects.get(`git/commits/${revisions[index]}`).tree.sha}`);
        objects.get(`git/blobs/${tree.tree[0].sha}`).content = Buffer.from('{').toString('base64');
      }, SyntaxError],
    );
  }
  for (const [name, mutate, expected] of mutations) {
    await assertHistoryRejected(() => ledgerHistoryFixture([clean, clean, clean], mutate), identity, expected, name);
  }
});

test('the pinned anchor is an exclusive resolved pre-seed boundary, not a ledger snapshot', async () => {
  const seed = state();
  seed.counter = '100';
  const identity = record(seed);
  const clean = publicLedger(seed);
  for (const parents of [[], [{ sha }], [{ sha }, { sha: newerSha }]]) {
    const fixture = ledgerHistoryFixture([clean], objects => {
      objects.get(`git/commits/${anchor}`).parents = parents;
    });
    const result = await fixture.store.read();
    assert.deepEqual(result.state, clean);
    assert.equal(result.revision, fixture.revisions[0]);
    assert.equal(fixture.calls.filter(call => call.endpoint === `git/commits/${anchor}`).length, 1);
    assert.equal(fixture.calls.some(call => call.endpoint === `git/trees/${'d'.repeat(40)}`), false,
      'Boundary tree/state and pre-boundary parents are deliberately outside the ledger chain');
    assert.equal(fixture.calls.filter(call => call.endpoint.startsWith('git/commits/')).length, 2);
  }
  await assertHistoryRejected(() => ledgerHistoryFixture([clean], objects => {
    objects.get('git/ref/heads/release-ledger').object.sha = anchor;
  }), identity, /seed is missing/, 'head equals checkpoint');
  const invalidSeed = structuredClone(clean);
  invalidSeed.extra = true;
  await assertHistoryRejected(() => ledgerHistoryFixture([invalidSeed]), identity,
    /persisted public ledger/, 'first child of checkpoint is fully validated');
});

test('history traversal never trusts a truncated compare list or skips old edges at a depth limit', async () => {
  const seed = state();
  const identity = record(seed);
  const clean = publicLedger(seed);
  const snapshots = Array.from({ length: 1005 }, () => clean);
  const fixture = ledgerHistoryFixture(snapshots);
  assert.deepEqual((await fixture.store.read()).state, clean);
  assert.equal(fixture.calls.filter(call => call.endpoint.startsWith('git/commits/')).length, snapshots.length + 1);
  const highWater = structuredClone(clean);
  highWater.counter = '100';
  snapshots[0] = highWater;
  await assertHistoryRejected(() => ledgerHistoryFixture(snapshots), identity,
    /counter rollback/, 'rollback beyond the first 1000 snapshots');
});

function protectionFixture(channel = 'insider', approvalMode = 'separation-of-duties') {
  const branch = channel === 'stable' ? 'main' : 'development';
  const names = ['release-canonical-tags', 'release-ledger-continuity', 'release-tag-creators', 'release-ledger-writer'];
  const rulesets = names.map((name, id) => ({
    id: id + 1, name, enforcement: 'active', privateMarker: 'raw-policy-sentinel',
    target: id % 2 === 0 ? 'tag' : 'branch',
    conditions: { ref_name: { include: [id % 2 === 0 ? 'refs/tags/v*' : 'refs/heads/release-ledger'], exclude: [] } },
    bypass_actors: id < 2 ? [] : [{ actor_type: 'Integration', actor_id: 123 }],
    rules: (id === 0 ? ['update', 'deletion'] : id === 1 ? ['non_fast_forward', 'deletion']
      : id === 2 ? ['creation'] : ['update']).map(type => ({ type })),
  }));
  const environment = { name: `release-${channel}`, privateMarker: 'raw-environment-sentinel',
    can_admins_bypass: false,
    deployment_branch_policy: { custom_branch_policies: true },
    protection_rules: [{ type: 'required_reviewers', prevent_self_review: true,
      reviewers: [{ type: 'User', reviewer: { id: 7, login: 'raw-reviewer-sentinel' } }] }] };
  if (approvalMode === 'single-maintainer') {
    environment.protection_rules[0].prevent_self_review = false;
    environment.protection_rules[0].reviewers[0].reviewer.login = 'jpapiez';
  }
  const branchRules = [
    { type: 'deletion' }, { type: 'non_fast_forward' },
    { type: 'pull_request', parameters: {
      require_code_owner_review: approvalMode === 'separation-of-duties',
      required_approving_review_count: approvalMode === 'separation-of-duties' ? 1 : 0,
      required_review_thread_resolution: true, require_last_push_approval: false,
      dismiss_stale_reviews_on_push: true,
    } },
    { type: 'required_status_checks', parameters: { strict_required_status_checks_policy: true,
      required_status_checks: releaseRequiredChecks.map(context => ({ context })) } },
  ].map(rule => ({ ...rule, ruleset_id: 5, ruleset_source_type: 'Repository', ruleset_source: context().repository }));
  const branchRuleset = { id: 5, name: 'protected-release-branches', enforcement: 'active', target: 'branch',
    bypass_actors: [], rules: branchRules,
    conditions: { ref_name: { include: ['refs/heads/main', 'refs/heads/development'], exclude: [] } } };
  const api = async (endpoint, method = 'GET') => {
    assert.equal(method, 'GET');
    if (endpoint === `rules/branches/${branch}?per_page=100`) return branchRules;
    if (endpoint === 'rulesets/5') return branchRuleset;
    if (endpoint === `environments/release-${channel}`) return environment;
    if (endpoint === `environments/release-${channel}/deployment-branch-policies`) {
      return { branch_policies: [{ name: 'development', type: 'branch' }] };
    }
    if (endpoint === 'rulesets?per_page=100') return rulesets;
    if (endpoint.startsWith('rulesets/')) return rulesets.find(rule => rule.id === Number(endpoint.split('/')[1]));
    throw new Error(endpoint);
  };
  return { api, environment, rulesets, branchRules, branchRuleset };
}

test('live protection adapter accepts only scoped reviewer-gated environments and exclusive publisher rules', async () => {
  const { api, environment, rulesets } = protectionFixture();
  const evidence = await verifyProtection(api, 'insider', '123', 'separation-of-duties');
  assert.equal(evidence.schema, 5);
  verifyProtectionEvidence(evidence, 'insider');
  assert.equal(evidence.claims.nonSelfApprovalRequired, true);
  assert.doesNotMatch(JSON.stringify(evidence), /rulesets|environment"|reviewers|publisherAppId|actor_id/);
  assert.throws(() => verifyProtectionEvidence(evidence, 'stable'), /mismatched/);
  await assert.rejects(verifyProtection(api, 'insider', '999', 'separation-of-duties'), /approved publisher app/);
  environment.protection_rules[0].prevent_self_review = false;
  await assert.rejects(verifyProtection(api, 'insider', '123', 'separation-of-duties'), /self-review setting/);
  environment.protection_rules[0].prevent_self_review = true;
  rulesets[0].bypass_actors.push({ actor_type: 'RepositoryRole', actor_id: 5 });
  await assert.rejects(verifyProtection(api, 'insider', '123', 'separation-of-duties'), /continuity bypass/);
  rulesets[0].bypass_actors = [];
  const staleListing = async endpoint => endpoint === 'rulesets?per_page=100'
    ? rulesets.map(rule => ({ ...rule, enforcement: 'active' })) : api(endpoint);
  rulesets[0].enforcement = 'disabled';
  await assert.rejects(verifyProtection(staleListing, 'insider', '123', 'separation-of-duties'), /active release-canonical-tags/);
});

for (const channel of ['stable', 'insider']) {
  for (const mode of ['single-maintainer', 'separation-of-duties']) {
    test(`${channel} ${mode} rejects administrator bypass or unproven bypass policy before reservation`, async () => {
      const previous = globalThis.fetch;
      try {
        for (const bypass of [true, undefined, null, 'false', 0, {}, []]) {
          const mutateEnvironment = environment => {
            if (bypass === undefined) delete environment.can_admins_bypass;
            else environment.can_admins_bypass = bypass;
          };
          const policy = protectionFixture(channel, mode);
          mutateEnvironment(policy.environment);
          await assert.rejects(verifyProtection(policy.api, channel, '123', mode),
            /explicitly disable administrator bypass/);
          const fixture = authorizationFixture(state(), { channel, approvalMode: mode, mutateEnvironment });
          globalThis.fetch = fixture.fetch;
          await assert.rejects(runFixtureControl('authorize', fixture),
            /explicitly disable administrator bypass/);
          assert.ok(fixture.calls.some(call => call.admin && call.publisher));
          assert.ok(fixture.calls.every(call => call.method === 'GET'), 'No reservation or source-tag write');
          assert.deepEqual(fixture.ledgerWrites, []);
        }
      } finally { globalThis.fetch = previous; }
    });

    test(`${channel} ${mode} requires manual approval and binds honest public-safe claims`, async () => {
      const fixture = protectionFixture(channel, mode);
      const evidence = await verifyProtection(fixture.api, channel, '123', mode);
      verifyProtectionEvidence(evidence, channel);
      assert.equal(evidence.approvalMode, mode);
      assert.equal(evidence.approvalAssurance,
        mode === 'single-maintainer' ? 'owner-confirmed/self-attested' : 'non-self-review-enforced');
      assert.equal(evidence.claims.manualApprovalRequired, true);
      assert.equal(evidence.claims.environmentAdminBypassBlocked, true);
      assert.equal(evidence.claims.nonSelfApprovalRequired, mode === 'separation-of-duties');
      assert.equal(evidence.claims.codeOwnerApprovalRequired, mode === 'separation-of-duties');
      assert.equal(evidence.claims.selfAttestedReviewRequired, true);
      assert.doesNotMatch(JSON.stringify(evidence), /jpapiez|raw-reviewer|reviewers|publisherAppId|actor_id/);
      const original = structuredClone(fixture.environment.protection_rules);
      for (const rules of [undefined, [], [original[0], original[0]],
        [{ ...original[0], reviewers: [] }], [{ ...original[0], reviewers: [{}] }],
        [{ ...original[0], reviewers: [{ type: 'User', reviewer: { id: 0, login: 'jpapiez' } }] }],
        [{ ...original[0], prevent_self_review: undefined }],
        [{ ...original[0], prevent_self_review: mode === 'single-maintainer' }]]) {
        fixture.environment.protection_rules = rules;
        await assert.rejects(verifyProtection(fixture.api, channel, '123', mode), /Owner blocker/);
      }
      fixture.environment.protection_rules = original;
      for (const mutate of [
        item => { delete item.approvalMode; },
        item => { item.approvalMode = 'unknown'; },
        item => { item.approvalAssurance = 'independent-approval'; },
        item => { item.claims.manualApprovalRequired = false; },
        item => { item.claims.environmentAdminBypassBlocked = false; },
        item => { delete item.claims.environmentAdminBypassBlocked; },
        item => { item.claims.nonSelfApprovalRequired = !item.claims.nonSelfApprovalRequired; },
        item => { item.claims.codeOwnerApprovalRequired = !item.claims.codeOwnerApprovalRequired; },
        item => { item.claims.selfAttestedReviewRequired = false; },
        item => { delete item.claims.pullRequestRequired; },
        item => { item.claims.branchBypassBlocked = false; },
        item => { item.claims.conversationResolutionRequired = false; },
        item => { item.schema = 2; item.policyProfile = 'printfarmer-release-protection/v1'; },
        item => { item.schema = 3; item.policyProfile = 'printfarmer-release-protection/v2'; },
        item => { item.schema = 4; item.policyProfile = 'printfarmer-release-protection/v3'; },
        item => { item.reviewers = ['private-reviewer']; },
      ]) {
        const changed = structuredClone(evidence);
        mutate(changed);
        const { policyDigest: ignored, ...payload } = changed;
        changed.policyDigest = hash(payload);
        assert.throws(() => verifyProtectionEvidence(changed, channel), ReleasePolicyError);
      }
    });
  }
}

test('single-maintainer accepts only owner-approved users and never fingerprints their identities', async () => {
  const fixture = protectionFixture('insider', 'single-maintainer');
  const rule = fixture.environment.protection_rules[0];
  const first = await verifyProtection(fixture.api, 'insider', '123', 'single-maintainer');
  const owner = structuredClone(rule.reviewers[0]);
  const other = { type: 'User', reviewer: { id: 99, login: 'private-delegate' } };
  for (const reviewers of [[other], [owner, other],
    [{ type: 'Team', reviewer: { id: 8, slug: 'private-team' } }]]) {
    rule.reviewers = reviewers;
    await assert.rejects(verifyProtection(fixture.api, 'insider', '123', 'single-maintainer'),
      /explicitly owner-approved/);
  }
  rule.reviewers = [other];
  const second = await verifyProtection(fixture.api, 'insider', '123', 'single-maintainer', '["private-delegate"]');
  const { policyDigest: ignoredFirst, verifiedAt: firstTime, ...a } = first;
  const { policyDigest: ignoredSecond, verifiedAt: secondTime, ...b } = second;
  assert.deepEqual(a, b);
  assert.doesNotMatch(JSON.stringify(second), /private-delegate|jpapiez/);
  rule.reviewers = [owner];
  for (const invalid of ['private-delegate', '[]', '{}', 'null', '[""]', '[7]', '["bad\\nlogin"]']) {
    await assert.rejects(verifyProtection(fixture.api, 'insider', '123', 'single-maintainer', invalid),
      error => error.message === 'Owner blocker: invalid owner-approved reviewer configuration');
  }
});

for (const channel of ['stable', 'insider']) {
  for (const mode of ['single-maintainer', 'separation-of-duties']) {
    test(`${channel} ${mode} branch policies fail closed without writes on missing, malformed or bypassed controls`, async () => {
      const previous = globalThis.fetch;
      const mutations = [
        ...['deletion', 'non_fast_forward', 'pull_request', 'required_status_checks'].map(type =>
          fixture => { fixture.branchRules.splice(fixture.branchRules.findIndex(rule => rule.type === type), 1); }),
        ...releaseRequiredChecks.map(context => fixture => {
          fixture.branchRules[3].parameters.required_status_checks =
            fixture.branchRules[3].parameters.required_status_checks.filter(check => check.context !== context);
        }),
        fixture => { fixture.branchRules[0].ruleset_id = undefined; },
        fixture => { fixture.branchRules.push(undefined); },
        fixture => { fixture.branchRules[2].parameters = undefined; },
        fixture => { fixture.branchRules[2].parameters.required_review_thread_resolution = false; },
        fixture => { fixture.branchRules[2].parameters.dismiss_stale_reviews_on_push = false; },
        fixture => { fixture.branchRules[2].parameters.require_code_owner_review = mode === 'single-maintainer'; },
        fixture => { fixture.branchRules[2].parameters.required_approving_review_count = mode === 'single-maintainer' ? 1 : 0; },
        ...[undefined, '0', '1', -1, 1.5, 7, true].map(count => fixture => {
          fixture.branchRules[2].parameters.required_approving_review_count = count;
        }),
        fixture => { fixture.branchRules[2].parameters.require_last_push_approval = undefined; },
        fixture => { fixture.branchRules[3].parameters.strict_required_status_checks_policy = false; },
        fixture => { fixture.branchRules[3].parameters.required_status_checks = [{ context: 'CI' }]; },
        fixture => { fixture.branchRules[3].parameters.required_status_checks.push({ context: ' private\ncheck' }); },
        fixture => { fixture.branchRules[3].parameters.required_status_checks[0].integration_id = '123'; },
        fixture => { fixture.branchRuleset.bypass_actors = [{ actor_type: 'RepositoryRole', actor_id: 5, bypass_mode: 'always' }]; },
        fixture => { fixture.branchRuleset.bypass_actors = [{ actor_type: 'Integration', actor_id: 123, bypass_mode: 'pull_request' }]; },
        fixture => { delete fixture.branchRuleset.bypass_actors; },
        fixture => { fixture.branchRuleset.enforcement = 'evaluate'; },
        fixture => { fixture.branchRuleset.target = 'tag'; },
        fixture => { fixture.branchRuleset.conditions.ref_name.include = ['refs/heads/other']; },
        fixture => { fixture.branchRuleset.conditions.ref_name.exclude = ['refs/heads/main']; },
        fixture => { fixture.branchRuleset.rules = []; },
      ];
      if (mode === 'single-maintainer') mutations.push(fixture => {
        fixture.branchRules[2].parameters.require_last_push_approval = true;
      });
      try {
        for (const mutatePolicy of mutations) {
          const fixture = authorizationFixture(state(), { channel, approvalMode: mode, mutatePolicy });
          globalThis.fetch = fixture.fetch;
          await assert.rejects(runFixtureControl('authorize', fixture),
            /Owner blocker|Incomplete|Missing successful exact-SHA required qualification/);
          assert.ok(fixture.calls.every(call => call.method === 'GET'));
          assert.deepEqual(fixture.ledgerWrites, []);
        }
      } finally { globalThis.fetch = previous; }
    });
  }
}

test('required review context is the exact status producer, never the successful workflow/check suite', async () => {
  assert.equal(releaseReviewStatus, 'squad/pre-pr-verdict');
  assert.deepEqual(releaseRequiredChecks, ['CI tooling tests', '.NET build', 'Frontend build & tests', 'squad/pre-pr-verdict']);
  const gate = await import('../squad-verdict-gate.mjs');
  assert.equal(releaseReviewStatus, gate.verdictContext);
  const producer = readFileSync('.github/workflows/squad-review-verdict.yml', 'utf8');
  assert.match(producer, /github\.rest\.repos\.createCommitStatus\(\{[\s\S]*?sha: headSha,[\s\S]*?context: gate\.verdictContext/);
  assert.match(producer, /const headSha = String\(pull\.head\?\.sha/);
  for (const name of releaseBuildChecks) {
    assert.ok(readFileSync('.github/workflows/ci.yml', 'utf8').includes(`name: ${name}`));
  }
});

test('legacy exact-SHA required-check adapter rejects malformed or failing statuses and checks', async () => {
  const previous = globalThis.fetch;
  const mutations = [
    { mutateStatuses: status => { status.sha = newerSha; } },
    { mutateStatuses: status => { status.statuses = []; status.total_count = 0; } },
    { mutateStatuses: status => { status.total_count = 100; } },
    { mutateStatuses: status => { status.total_count = '1'; } },
    { mutateStatuses: status => { status.statuses[0].context = 'squad/pre-pr-review'; } },
    ...[undefined, '', `NOT_APPLICABLE @ ${sha.slice(0, 12)}: not a squad PR`,
      `REVIEWED (self-attested) @ ${newerSha.slice(0, 12)} by fixture`].map(description => ({
      mutateStatuses: status => { status.statuses[0].description = description; },
    })),
    ...['pending', 'failure', 'error', undefined].map(state => ({
      mutateStatuses: status => { status.statuses[0].state = state; },
    })),
    { mutateStatuses: status => {
      status.statuses.push({ id: 2, context: releaseReviewStatus, state: 'failure' }); status.total_count = 2;
    } },
    { mutateStatuses: status => { status.statuses[0].id = '1'; } },
    { mutateChecks: checks => { checks.check_runs[0].head_sha = newerSha; } },
    { mutateChecks: checks => { delete checks.check_runs[0].head_sha; } },
    { mutateChecks: checks => { checks.check_runs[0].conclusion = 'failure'; } },
    { mutateChecks: checks => {
      checks.check_runs[0].status = 'in_progress'; checks.check_runs[0].check_suite = { conclusion: 'success' };
    } },
    { mutateChecks: checks => { checks.check_runs[0].app.slug = 'untrusted'; } },
    { mutateChecks: checks => { checks.total_count = 100; } },
    { mutateChecks: checks => { checks.check_runs.pop(); } },
    { mutateChecks: checks => {
      checks.check_runs.push({ ...checks.check_runs[0], id: 99, conclusion: 'failure' }); checks.total_count++;
    } },
    { mutateStatuses: status => {
      status.statuses.push({ id: 2, context: releaseBuildChecks[0], state: 'failure' }); status.total_count++;
    } },
    { mutateChecks: checks => {
      checks.check_runs.push({ ...checks.check_runs[0], name: releaseReviewStatus, id: 99, conclusion: 'failure' });
      checks.total_count++;
    } },
    { mutateStatuses: status => { status.statuses = []; status.total_count = 0; },
      mutateChecks: checks => {
        checks.check_runs.push({ ...checks.check_runs[0], name: releaseReviewStatus, id: 99 }); checks.total_count++;
      } },
  ];
  try {
    for (const approvalMode of ['single-maintainer', 'separation-of-duties']) {
        for (const mutation of mutations) {
          const fixture = authorizationFixture(state(), { approvalMode, ...mutation });
          globalThis.fetch = fixture.fetch;
          await assert.rejects(verifyReleaseChecks(githubClient(fixture.env.GH_TOKEN), sha),
            /Invalid|qualification|evidence|stale/);
          assert.ok(fixture.calls.every(call => call.method === 'GET'));
          assert.deepEqual(fixture.ledgerWrites, []);
      }
    }
  } finally { globalThis.fetch = previous; }
});

test('additional configured checks enforce latest exact run and integration binding without inventing status fields', async () => {
  const evidenceAt = new Date().toISOString();
  const checks = { total_count: 2, check_runs: [
    { id: 1, name: 'extra', head_sha: sha, status: 'completed', conclusion: 'failure',
      completed_at: evidenceAt, app: { id: 123 } },
    { id: 2, name: 'extra', head_sha: sha, status: 'completed', conclusion: 'success',
      completed_at: evidenceAt, app: { id: 123 } },
  ] };
  const statuses = { sha, total_count: 0, statuses: [] };
  const api = async endpoint => endpoint.includes('/check-runs?') ? checks : statuses;
  await verifyReleaseChecks(api, sha, [{ context: 'extra', integration_id: 123 }]);
  await assert.rejects(verifyReleaseChecks(api, sha, [{ context: 'extra', integration_id: 999 }]), /qualification/);
  statuses.statuses.push({ id: 1, context: 'extra', state: 'failure',
    created_at: evidenceAt, updated_at: evidenceAt }); statuses.total_count++;
  await assert.rejects(verifyReleaseChecks(api, sha, [{ context: 'extra' }]), /qualification/);
  statuses.statuses[0].state = 'success';
  await verifyReleaseChecks(api, sha, [{ context: 'extra' }]);
  checks.check_runs = []; checks.total_count = 0;
  await verifyReleaseChecks(api, sha, [{ context: 'extra' }]);
  await assert.rejects(verifyReleaseChecks(api, sha, [{ context: 'extra', integration_id: 123 }]), /qualification/);
  const previous = globalThis.fetch;
  try {
    const fixture = authorizationFixture(state(), { mutatePolicy: fixture => {
      fixture.branchRules[3].parameters.required_status_checks.push({ context: 'extra' });
    } });
    globalThis.fetch = fixture.fetch;
    await assert.rejects(runFixtureControl('authorize', fixture), /qualification/);
    assert.deepEqual(fixture.ledgerWrites, []);
  } finally { globalThis.fetch = previous; }
});

test('GitHub evidence timestamps accept seconds or milliseconds and reject unsupported REST shapes', async () => {
  const collectedAt = Date.parse('2026-09-13T20:00:00.000Z');
  for (const value of ['2026-09-13T19:30:00Z', '2026-09-13T19:30:00.123Z']) {
    assert.equal(parseGithubTimestamp(value), Date.parse(value));
    const checks = { total_count: 1, check_runs: [{
      id: 1, name: 'extra', head_sha: sha, status: 'completed', conclusion: 'success',
      completed_at: value, app: { id: 123 },
    }] };
    const statuses = { sha, total_count: 0, statuses: [] };
    await verifyReleaseChecks(
      async endpoint => endpoint.includes('/check-runs?') ? checks : statuses,
      sha,
      [{ context: 'extra', integration_id: 123 }],
      collectedAt,
    );
  }
  for (const value of [
    '2026-09-13T19:30:00+00:00',
    '2026-09-13T19:30:00-07:00',
    '2026-09-13T19:30:00.1234Z',
    '2026-09-13T19:30:00.Z',
    '2026-02-30T19:30:00Z',
    '2026-09-13T24:00:00Z',
    '0000-09-13T19:30:00Z',
    '2026-09-13T19:30:00',
    '2026-09-13t19:30:00z',
    '',
  ]) assert.throws(() => parseGithubTimestamp(value), /Invalid GitHub timestamp/);
});

test('read-only admission does not require a pre-existing canonical qualification status or checks', async () => {
  const previous = globalThis.fetch;
  const previousOutput = process.env.GITHUB_OUTPUT;
  delete process.env.GITHUB_OUTPUT;
  try {
    for (const channel of ['stable', 'insider']) {
      const fixture = authorizationFixture(state(), { channel });
      globalThis.fetch = fixture.fetch;
      await runReleaseControl('admit', fixture.env);
      assert.ok(fixture.calls.every(call => call.method === 'GET'));
      assert.ok(fixture.calls.every(call => !/actions\/|commits\/.*\/(?:status|check-runs)/.test(call.endpoint)));
    }
  } finally {
    globalThis.fetch = previous;
    if (previousOutput !== undefined) process.env.GITHUB_OUTPUT = previousOutput;
  }
});

test('missing or unknown approval mode cannot read policy or admit or authorize a release', async () => {
  const fixture = authorizationFixture();
  const previous = globalThis.fetch;
  globalThis.fetch = () => assert.fail('Invalid mode must fail before network access');
  try {
    for (const mode of [undefined, '', 'unknown', 'Single-maintainer', 'single-maintainer ', 'single-maintainer\n', true]) {
      await assert.rejects(verifyProtection(() => assert.fail('No policy read'), 'insider', '123', mode),
        /RELEASE_APPROVAL_MODE/);
      for (const operation of ['admit', 'authorize']) {
        await assert.rejects(runFixtureControl(operation, fixture, {
          ...fixture.env, RELEASE_APPROVAL_MODE: mode,
        }),
          /RELEASE_APPROVAL_MODE/);
      }
    }
  } finally { globalThis.fetch = previous; }
});

test('release workflow explicitly wires approval mode and confines reviewer evidence to protected authorization', () => {
  const authority = readFileSync('.github/workflows/consolidated-release.yml', 'utf8');
  const publisher = readFileSync('.github/workflows/docker-publish.yml', 'utf8');
  const admissionJob = authority.split('  admit:')[1].split('  qualification:')[0];
  assert.match(admissionJob, /RELEASE_APPROVAL_MODE: \$\{\{ vars\.RELEASE_APPROVAL_MODE \}\}/);
  assert.doesNotMatch(admissionJob, /RELEASE_OWNER_APPROVED_REVIEWERS/);
  assert.match(admissionJob, /approval_mode: \$\{\{ steps\.admit\.outputs\.approval_mode \}\}/);
  assert.match(publisher,
    /RELEASE_ADMITTED_APPROVAL_MODE: \$\{\{ fromJSON\(inputs\.transaction\)\.approvalMode \}\}/);
  assert.match(publisher,
    /environment: \$\{\{ inputs\.operation == 'abandon' && 'release-insider' \|\| \(fromJSON\(inputs\.transaction\)\.channel == 'stable' && 'release-stable' \|\| 'release-insider'\) \}\}/);
  assert.match(publisher,
    /RELEASE_OWNER_APPROVED_REVIEWERS: \$\{\{ secrets\.RELEASE_OWNER_APPROVED_REVIEWERS \}\}/);
  assert.match(admissionJob, /statuses: read/);
  assert.match(publisher, /permission-statuses: read/);
});

test('authorization fails closed on missing, invalid or divergent admitted approval mode before API access', async () => {
  const previous = globalThis.fetch;
  globalThis.fetch = () => assert.fail('Approval-mode divergence must fail before network access');
  try {
    for (const mode of ['single-maintainer', 'separation-of-duties']) {
      const fixture = authorizationFixture(state(), { approvalMode: mode });
      for (const admittedMode of [undefined, '', 'unknown', `${mode} `,
        mode === 'single-maintainer' ? 'separation-of-duties' : 'single-maintainer']) {
        await assert.rejects(runFixtureControl('authorize', fixture, {
          ...fixture.env, RELEASE_ADMITTED_APPROVAL_MODE: admittedMode,
        }), /RELEASE_APPROVAL_MODE|Approval mode changed after admission/);
        assert.deepEqual(fixture.ledgerWrites, []);
      }
    }
  } finally { globalThis.fetch = previous; }
});

test('single-maintainer authorization rejects unapproved, automatic and conflicting policies before writes', async () => {
  const previous = globalThis.fetch;
  try {
    for (const mutateEnvironment of [
      env => { env.protection_rules = []; },
      env => { env.protection_rules[0].reviewers = []; },
      env => { env.protection_rules[0].reviewers[0].reviewer.login = 'unapproved'; },
      env => { env.protection_rules[0].prevent_self_review = true; },
      env => { env.deployment_branch_policy.custom_branch_policies = false; },
    ]) {
      const fixture = authorizationFixture(state(), { approvalMode: 'single-maintainer', mutateEnvironment });
      globalThis.fetch = fixture.fetch;
      await assert.rejects(runFixtureControl('authorize', fixture), /Owner blocker/);
      assert.ok(fixture.calls.length > 0);
      assert.ok(fixture.calls.every(call => call.method === 'GET'));
      assert.deepEqual(fixture.ledgerWrites, []);
    }
  } finally { globalThis.fetch = previous; }
});

test('protected release-control abandonment uses App policy verification and GitHub CAS without exposing authorization data', async () => {
  const cwd = process.cwd();
  const root = resolve('.artifacts', `abandon-control-${process.pid}`);
  const previousFetch = globalThis.fetch;
  mkdirSync(root, { recursive: true });
  process.chdir(root);
  try {
    const fixture = authorizationFixture(state(), { includeAbandonmentProof: true });
    globalThis.fetch = fixture.fetch;
    const identity = await runFixtureControl('authorize', fixture);
    const recovered = await runReleaseControl('recover-abandonment', {
      ...fixture.env, RELEASE_ABANDONMENT_TARGET: identity.allocationKey,
    }, () => {});
    assert.equal(recovered.identitySha256, hash(identity));
    fixture.abandonmentJob.started_at = new Date().toISOString();
    fixture.deleteTag();
    fixture.setCanonicalHead('f'.repeat(40));
    fixture.abandonmentJobs.push({ ...fixture.abandonmentJob, id: 901 });
    await assert.rejects(runReleaseControl('abandon', {
      ...fixture.env,
      RELEASE_PUBLIC_IDENTITY: JSON.stringify(publicAuthorization(identity)),
      RELEASE_ABANDONMENT_TARGET: identity.allocationKey,
    }, () => {}), /protected job evidence is missing, ambiguous, or mismatched/, 'ambiguous protected job');
    fixture.abandonmentJobs.pop();
    fixture.calls.length = 0;
    const stableTransaction = JSON.parse(fixture.env.RELEASE_TRANSACTION);
    stableTransaction.channel = 'stable';
    await assert.rejects(runReleaseControl('abandon', {
      ...fixture.env,
      RELEASE_TRANSACTION: JSON.stringify(stableTransaction),
      RELEASE_PUBLIC_IDENTITY: JSON.stringify(publicAuthorization(identity)),
      RELEASE_ABANDONMENT_TARGET: identity.allocationKey,
    }, () => {}), /Only insider release transactions|Invalid release transaction branch binding/, 'stable transaction cannot abandon');
    await assert.rejects(runReleaseControl('abandon', {
      ...fixture.env, GITHUB_RUN_ATTEMPT: '2',
      RELEASE_PUBLIC_IDENTITY: JSON.stringify(publicAuthorization(identity)),
      RELEASE_ABANDONMENT_TARGET: identity.allocationKey,
    }, () => {}), /initial protected workflow attempt/, 'historical approval cannot authorize a rerun');
    for (const [name, mutate] of [
      ['spoofed approver', value => { value.user.login = 'outsider'; }],
      ['wrong environment', value => { value.environments[0].name = 'release-stable'; }],
      ['missing approval', value => { value.state = 'rejected'; }],
    ]) {
      const original = structuredClone(fixture.approvalEvidence);
      mutate(fixture.approvalEvidence);
      await assert.rejects(runReleaseControl('abandon', {
        ...fixture.env,
        RELEASE_PUBLIC_IDENTITY: JSON.stringify(publicAuthorization(identity)),
        RELEASE_ABANDONMENT_TARGET: identity.allocationKey,
      }, () => {}), /Abandonment requires approval from an allowed owner/, name);
      Object.assign(fixture.approvalEvidence, original);
    }
    const wrongRun = { ...fixture.env, GITHUB_RUN_ID: '43' };
    await assert.rejects(runReleaseControl('abandon', {
      ...wrongRun,
      RELEASE_PUBLIC_IDENTITY: JSON.stringify(publicAuthorization(identity)),
      RELEASE_ABANDONMENT_TARGET: identity.allocationKey,
    }, () => {}), /executing trusted workflow/, 'wrong current approval run');
    await runReleaseControl('abandon', {
      ...fixture.env,
      RELEASE_PUBLIC_IDENTITY: JSON.stringify(publicAuthorization(identity)),
      RELEASE_ABANDONMENT_TARGET: identity.allocationKey,
    }, () => {});
    const persisted = (await gitLedger(githubClient(fixture.env.RELEASE_PUBLISHER_TOKEN), anchor).read()).state;
    const terminal = persisted.reservations[identity.allocationKey].abandonment;
    assert.deepEqual(Object.keys(terminal).sort(), [
      'allocationKey', 'approvalEnvironment', 'approvalJobId', 'approvalRunAttempt', 'approvalRunId', 'approvalTarget',
    'canonicalVersion', 'channel', 'identitySha256', 'ownerApprovedAt', 'schema', 'sourceCommit',
    ]);
    assert.ok(fixture.calls.some(call => call.admin && call.publisher));
    assert.ok(fixture.calls.some(call => call.method === 'PATCH' && call.publisher));
    assert.doesNotMatch(JSON.stringify(fixture.ledgerWrites.at(-1)), /protectionDigest|reviewer|private/i);
    await assert.rejects(runReleaseControl('abandon', {
      ...fixture.env,
      RELEASE_PUBLIC_IDENTITY: JSON.stringify(publicAuthorization(identity)),
      RELEASE_ABANDONMENT_TARGET: identity.allocationKey,
    }, () => {}), /(cannot be reactivated or abandoned twice|Terminally abandoned reservation)/);
    const subsequent = reserve(persisted, admission({ buildId: '43' }), created).record;
    assert.equal(subsequent.sequence, '2');
  } finally {
    globalThis.fetch = previousFetch;
    process.chdir(cwd);
    rmSync(root, { recursive: true, force: true });
  }
});

async function authorizedRecord() {
  const protection = await verifyProtection(protectionFixture().api, 'insider', '123', 'separation-of-duties');
  return { ...record(), created: protection.verifiedAt, protection };
}

const privateFields = /"(?:branchRules|branchRulesets|environment|branchPolicies|rulesets|reviewers|publisherAppId|actor_id|bypass_actors|can_admins_bypass|rules|privateMarker|futurePrivate|reviewerId)"|raw-(?:policy|environment|reviewer)-sentinel|private-value/;

function artifactUploads(workflow) {
  const text = readFileSync(workflow, 'utf8');
  return [...text.matchAll(/uses: actions\/upload-artifact@[^\n]+\n([\s\S]*?)(?=^      -|^  [\w-]+:|(?![\s\S]))/gm)]
    .map(([, step]) => {
      const [, path] = step.match(/^          path: (.+)$/m) || [];
      assert.ok(path, 'Every upload must declare paths');
      return path.trim() === '|'
        ? [...step.matchAll(/^            ([^\n]+)$/gm)].map(([, value]) => value.trim())
        : [path.trim()];
    });
}

test('every release artifact upload path is explicitly inventoried, including both signed handoffs', () => {
  const authority = '.github/workflows/consolidated-release.yml';
  const docker = '.github/workflows/docker-publish.yml';
  assert.deepEqual(artifactUploads(authority), [
    ['.artifacts/release-transaction/transaction.json'],
    ['.artifacts/release-transaction/qualification.json'],
  ]);
  assert.deepEqual(artifactUploads(docker), [
    [
      authorizationPath, authorizationBundle,
      '.artifacts/release-authorization/public-identity.json',
      '.artifacts/release-authorization/public-identity.bundle.json',
    ],
    [
      manifestPath, manifestEnvelopePath, manifestEnvelopeBundle,
      '.artifacts/release-authorization/release-crypto-evidence.json',
      '.artifacts/release-authorization/release-notes.md',
    ],
    [
      authorizationPath, authorizationBundle, privateSetPath,
      manifestPath, manifestEnvelopePath, manifestEnvelopeBundle,
    ],
  ]);
  // Keep the call graph closed: a new reusable workflow/action must be inventoried too.
  for (const file of [authority, docker]) {
    const text = readFileSync(file, 'utf8');
    assert.equal(artifactUploads(file).length, (text.match(/uses:\s*actions\/upload-artifact@/g) || []).length);
    for (const [, local] of text.matchAll(/uses: \.\/([^\s]+)/g)) {
      assert.ok([
        '.github/workflows/ci.yml',
        '.github/workflows/docker-publish.yml',
      ].includes(local), local);
    }
    assert.doesNotMatch(text, /(?:gh api|curl).*(?:rulesets|environments|rules\/branches)|sign\.log/);
  }
});

test('normalized attestation and artifact writers reject raw, unknown and weakened fields before persistence', async () => {
  const identity = await authorizedRecord();
  const root = resolve('.artifacts', `normalized-writers-${process.pid}`);
  const cwd = process.cwd();
  mkdirSync(root, { recursive: true });
  process.chdir(root);
  try {
    for (const [key, value] of [
      ['rulesets', [{ id: 7 }]], ['publisherAppId', '123'], ['reviewerId', 'private-value'],
      ['futurePrivate', { secret: 'private-value' }],
    ]) {
      assert.throws(() => writeAuthorization({ ...identity, [key]: value }), /authorization fields/);
      assert.throws(() => writeAuthorization({ ...identity,
        protection: { ...identity.protection, [key]: value } }), /normalized protection/);
      assert.equal(existsSync(authorizationPath), false);
    }
    for (const claim of Object.keys(identity.protection.claims)) {
      for (const value of [false, 1, 'true', undefined]) {
        const weakened = structuredClone(identity);
        weakened.protection.claims[claim] = value;
        const { policyDigest: ignored, ...payload } = weakened.protection;
        weakened.protection.policyDigest = hash(payload);
        assert.throws(() => writeAuthorization(weakened), /claims missing or weakened/);
      }
      for (const field of Object.keys(identity).filter(field => field !== 'protection')) {
        assert.throws(() => writeAuthorization({ ...identity, [field]: { privateMarker: 'private-value' } }));
        assert.equal(existsSync(authorizationPath), false);
      }
    }
    writeAuthorization(identity);
    const set = completeSet(identity);
    set.futurePrivate = { secret: 'private-value' };
    set.images.api.platforms['linux/amd64'].labels.reviewerId = 'private-value';
    assert.throws(() => writeAuthorizationSet(identity, set), /public set fields|public set labels/);
    assert.equal(existsSync(privateSetPath), false);
    writeAuthorizationSet(identity, completeSet(identity));
    const env = { GITHUB_REPOSITORY: context().repository, GITHUB_REF: context().ref,
      RELEASE_SIGNER_IDENTITY: publisherWorkflowIdentity,
      RELEASE_PUBLIC_IDENTITY: JSON.stringify(publicAuthorization(identity)) };
    const changed = structuredClone(identity);
    changed.protection.claims.canonicalTagsImmutable = false;
    const { policyDigest: ignored, ...payload } = changed.protection;
    changed.protection.policyDigest = hash(payload);
    writeFileSync(authorizationPath, JSON.stringify(changed));
    assert.throws(() => verifyAuthorization(env, () => {}), /differs from public identity/);
    env.RELEASE_PUBLIC_IDENTITY = JSON.stringify(publicAuthorization(changed));
    assert.throws(() => verifyAuthorization(env, () => {}), /claims missing or weakened/);
  } finally {
    process.chdir(cwd);
    rmSync(root, { recursive: true, force: true });
  }
});

test('stable artifact qualification retains pass claims but never owner free text or reviewer identities', async () => {
  const protection = await verifyProtection(protectionFixture().api, 'insider', '123', 'separation-of-duties');
  const { policyDigest: ignored, ...payload } = {
    ...protection, channel: 'stable', branch: 'main',
  };
  const stableProtection = { ...payload, policyDigest: hash(payload) };
  const stable = admit(context({ channel: 'stable' }), sha, 'v1.2.3');
  const ledger = state();
  ledger.qualifications[sha] = hotfixQualification();
  const qualification = await verifyStableQualification(() => assert.fail('No hotfix tree lookup'), ledger, stable);
  const identity = reserve(ledger, stable, protection.verifiedAt, stableProtection, qualification).record;
  assert.equal(identity.qualification.mode, 'hotfix');
  const cwd = process.cwd();
  const root = resolve('.artifacts', `stable-normalized-${process.pid}`);
  mkdirSync(root, { recursive: true });
  process.chdir(root);
  try {
    writeAuthorization(identity);
    assert.doesNotMatch(readFileSync(authorizationPath, 'utf8'), privateFields);
    const original = readFileSync(authorizationPath, 'utf8');
    for (const field of ['reviewers', 'hotfixReason', 'futurePrivate']) {
      const changed = structuredClone(identity);
      changed.qualification[field] = 'private-value';
      assert.throws(() => writeAuthorization(changed), /public ledger qualification/);
      assert.equal(readFileSync(authorizationPath, 'utf8'), original);
    }
  } finally {
    process.chdir(cwd);
    rmSync(root, { recursive: true, force: true });
  }
});

test('raw policy identity changes do not affect normalized digests and API errors cannot disclose raw payloads', async () => {
  const fixture = protectionFixture();
  const first = await verifyProtection(fixture.api, 'insider', '123', 'separation-of-duties');
  fixture.environment.protection_rules[0].reviewers = [
    { type: 'User', reviewer: { id: 999, login: 'another-private-reviewer' } },
  ];
  for (const rule of fixture.rulesets) {
    rule.privateMarker = 'changed-private-rule';
    if (rule.bypass_actors.length) rule.bypass_actors[0].actor_id = 456;
  }
  const second = await verifyProtection(fixture.api, 'insider', '456', 'separation-of-duties');
  const { policyDigest: ignoredFirst, verifiedAt: firstTime, ...a } = first;
  const { policyDigest: ignoredSecond, verifiedAt: secondTime, ...b } = second;
  assert.deepEqual(a, b, 'No raw policy data or identity fingerprint may survive normalization');
  const { policyDigest: digest, ...payload } = first;
  assert.equal(digest, hash(payload));
  for (const endpointPrefix of ['rules/branches', 'environments/', 'rulesets']) {
    await assert.rejects(verifyProtection(async endpoint => {
      if (endpoint.startsWith(endpointPrefix)) throw new Error('raw-reviewer-sentinel private-value');
      return fixture.api(endpoint);
    }, 'insider', '456', 'separation-of-duties'), error => error.message === 'Protection policy read failed');
  }
});

function authorizationFixture(initial = state(), settings = {}) {
  const channel = settings.channel ?? 'insider';
  const sourceCommit = settings.sourceCommit ?? sha;
  const observedBranchHead = settings.observedBranchHead ?? sourceCommit;
  const workflowCommit = settings.workflowCommit ?? workflowControlSha;
  const currentBranchHead = settings.currentBranchHead ?? observedBranchHead;
  const branch = channel === 'stable' ? 'main' : 'development';
  const selected = context({
    channel, eventSha: sourceCommit, workflowSha: workflowCommit, observedBranchHead,
    ref: 'refs/heads/development', workflowBranch: 'development',
  });
  const policy = protectionFixture(channel, settings.approvalMode);
  const { api: policies, environment, branchRules } = policy;
  branchRules.find(rule => rule.type === 'required_status_checks').parameters.required_status_checks
    .push(...canonicalValidationChecks.map(context => ({ context })));
  settings.mutateEnvironment?.(environment);
  settings.mutatePolicy?.(policy);
  const trees = promotionApi(settings.treeOptions);
  let comparedTree = false;
  const objects = new Map();
  objects.set(anchor, { tree: { sha: anchor }, parents: [] });
  let serial = 100;
  const put = value => {
    const id = (++serial).toString(16).padStart(40, '0');
    objects.set(id, value);
    return id;
  };
  let head = anchor;
  const appendSnapshot = snapshot => {
    const blob = put({ encoding: 'base64', content: Buffer.from(JSON.stringify(snapshot)).toString('base64') });
    const tree = put({ truncated: false, tree: [{ path: 'state.json', type: 'blob', sha: blob }] });
    head = put({ tree: { sha: tree }, parents: [{ sha: head }] });
  };
  for (const snapshot of settings.history ?? [initial]) appendSnapshot(snapshot);
  let advanced = false;
  let tag;
  let canonicalHead = currentBranchHead;
  let canonicalComparison = { status: 'ahead', merge_base_commit: { sha: sourceCommit } };
  const calls = [];
  const ledgerWrites = [];
  const env = {
    GH_TOKEN: 'github-fixture', RELEASE_PUBLISHER_TOKEN: 'publisher-fixture',
    RELEASE_PUBLISHER_APP_ID: '123', RELEASE_LEDGER_ANCHOR: anchor,
    RELEASE_APPROVAL_MODE: settings.approvalMode ?? 'separation-of-duties',
    RELEASE_ADMITTED_APPROVAL_MODE: settings.approvalMode ?? 'separation-of-duties',
    RELEASE_OWNER_APPROVED_REVIEWERS: '[\"jpapiez\"]',
    GITHUB_REPOSITORY: selected.repository, GITHUB_EVENT_NAME: selected.event,
    GITHUB_REF: selected.ref, GITHUB_SHA: workflowCommit,
    GITHUB_WORKFLOW_REF: selected.workflowIdentity, GITHUB_WORKFLOW_SHA: workflowCommit,
    GITHUB_RUN_ID: '42', GITHUB_RUN_ATTEMPT: '1', RELEASE_CHANNEL: channel,
    RELEASE_SIGNER_IDENTITY: publisherWorkflowIdentity,
  };
  const transaction = {
    kind: 'release-transaction',
    schema: 2,
    repository: selected.repository,
    channel,
    sourceBranch: branch,
    sourceCommit,
    observedBranchHead,
    workflowIdentity: selected.workflowIdentity,
    workflowCommit,
    runId: '42',
    runAttempt: '1',
    approvalMode: env.RELEASE_APPROVAL_MODE,
  };
  env.RELEASE_TRANSACTION = JSON.stringify(transaction);
  env.RELEASE_SOURCE_COMMIT = sourceCommit;
  const reviewedHead = settings.reviewedHead ?? sourceCommit;
  const canonical = sourceReviewFixture(sourceCommit, branch, Date.now(), reviewedHead);
  settings.mutateReview?.(canonical);
  const qualificationStartedAt = new Date(Date.now() - 4 * 60_000).toISOString();
  const qualificationCompletedAt = new Date(Date.now() - 2 * 60_000).toISOString();
  const qualificationRunUrl = `https://github.com/${selected.repository}/actions/runs/42`;
  const approvalEvidence = settings.approvalEvidence ?? {
    state: 'approved', user: { login: 'jpapiez' }, submitted_at: new Date().toISOString(),
    environments: [{ name: 'release-insider' }],
  };
  const abandonmentJob = { id: 900, name: 'Approve and execute immutable release operation / Abandon immutable release reservation', run_id: 42,
    run_attempt: 1, status: 'in_progress', started_at: qualificationCompletedAt, conclusion: undefined };
  const abandonmentJobs = [abandonmentJob];
  const transactionJobs = qualificationJobs.map((name, index) => ({
    id: 1000 + index,
    name: `${qualificationJobNamespace} / ${name}`,
    run_id: 42,
    run_attempt: 1,
    head_sha: workflowCommit,
    status: 'completed',
    conclusion: 'success',
    started_at: qualificationStartedAt,
    completed_at: qualificationCompletedAt,
    check_run_url:
      `https://api.github.com/repos/${selected.repository}/check-runs/${1000 + index}`,
    html_url: `${qualificationRunUrl}/job/${1000 + index}`,
  }));
  return {
    env, calls, ledgerWrites, environment, branchRules, approvalEvidence, abandonmentJob, abandonmentJobs,
    transactionJobs, review: canonical,
    deleteTag() { tag = undefined; },
    setCanonicalHead(value) { canonicalHead = value; },
    setCanonicalComparison(value) { canonicalComparison = value; },
    async fetch(url, options) {
      const endpoint = url.replace(/^https:\/\/api\.github\.com\/repos\/OlyForge3D\/PrintFarmer\/?/, '');
      assert.notEqual(endpoint, url, `Unexpected API host/path: ${url}`);
      const method = options.method;
      const publisher = options.headers.Authorization === `Bearer ${env.RELEASE_PUBLISHER_TOKEN}`;
      const admin = /^(rulesets|environments\/)/.test(endpoint);
      calls.push({ endpoint, method, publisher, admin });
      const response = (body, status = 200) => new Response(JSON.stringify(body), { status });
      if ((admin || method !== 'GET') && !publisher) return response({}, 403);
      if (endpoint.startsWith('rules/branches/')) return response(await policies(endpoint));
      if (admin) {
        if (settings.advanceBeforeAllocation && !advanced) {
          appendSnapshot(settings.advanceBeforeAllocation);
          advanced = true;
        }
        return response(await policies(endpoint));
      }
      if (endpoint === `git/ref/heads/${branch}`) return response({
        ref: `refs/heads/${branch}`,
        object: { type: 'commit', sha: settings.headDriftAfterTree && comparedTree ? 'f'.repeat(40) : canonicalHead },
      });
      if (endpoint === 'git/ref/heads/development') return response({
        ref: 'refs/heads/development', object: { type: 'commit', sha: workflowCommit },
      });
      if (endpoint === 'git/ref/heads/release-ledger') return response({ object: { sha: head } });
      if (endpoint === `compare/${sourceCommit}...${canonicalHead}`) return response(canonicalComparison);
      if (endpoint.startsWith('compare/')) return response({ status: 'ahead' });
      if (endpoint.startsWith('contents/VERSION?')) {
        return response({ encoding: 'base64', content: Buffer.from('v1.2.3\n').toString('base64') });
      }
      if (endpoint === 'actions/workflows/consolidated-release.yml') {
        return response({
          id: 9,
          path: '.github/workflows/consolidated-release.yml',
          state: 'active',
        });
      }
      if (endpoint === 'actions/runs/42') {
        return response({
          id: 42,
          run_attempt: 1,
          path: '.github/workflows/consolidated-release.yml',
          workflow_id: 9,
          repository: { full_name: selected.repository },
          head_repository: { full_name: selected.repository },
          head_branch: 'development',
          head_sha: workflowCommit,
          event: 'workflow_dispatch',
          html_url: qualificationRunUrl,
          status: 'in_progress',
          conclusion: undefined,
          actor: { login: 'author' },
          triggering_actor: { login: 'author' },
          check_suite_id: 100,
          run_started_at: qualificationStartedAt,
          updated_at: qualificationCompletedAt,
        });
      }
      if (endpoint === 'actions/runs/42/attempts/1/jobs?per_page=100') {
        const jobs = settings.includeAbandonmentProof ? [...transactionJobs, ...abandonmentJobs] : transactionJobs;
        return response({ total_count: jobs.length, jobs });
      }
      if (endpoint === 'actions/runs/42/approvals') {
        const approvals = settings.includeAbandonmentProof ? [approvalEvidence] : [];
        return response(approvals);
      }
      if (workflowCommit !== sourceCommit &&
        endpoint.startsWith(`commits/${workflowCommit}/check-runs`)) {
        const checks = {
          total_count: transactionJobs.length,
          check_runs: transactionJobs.map(job => ({
            id: job.id,
            name: job.name,
            head_sha: workflowCommit,
            status: 'completed',
            conclusion: 'success',
            completed_at: qualificationCompletedAt,
            app: { id: 15368, slug: 'github-actions' },
            check_suite: { id: 100 },
            url: job.check_run_url,
          })),
        };
        settings.mutateChecks?.(checks);
        return response(checks);
      }
      if (endpoint.startsWith(`commits/${sourceCommit}/check-runs`)) {
        const names = [...releaseBuildChecks, ...canonicalValidationChecks];
        const checkRuns = names
          .map((name, id) => ({ name, id: id + 1, head_sha: sourceCommit, status: 'completed',
            conclusion: 'success', app: { slug: 'github-actions' }, check_suite: { id: 100 },
            completed_at: new Date(Date.now() - 5 * 60_000).toISOString(),
            url: `https://api.github.com/repos/OlyForge3D/PrintFarmer/check-runs/${id + 1}` }));
        if (workflowCommit === sourceCommit) {
          checkRuns.push(...transactionJobs.map(job => ({
            id: job.id,
            name: job.name,
            head_sha: workflowCommit,
            status: 'completed',
            conclusion: 'success',
            completed_at: qualificationCompletedAt,
            app: { id: 15368, slug: 'github-actions' },
            check_suite: { id: 100 },
            url: job.check_run_url,
          })));
        }
        const checks = { total_count: checkRuns.length, check_runs: checkRuns };
        settings.mutateChecks?.(checks);
        return response(checks);
      }
      if ([`commits/${sourceCommit}/status?per_page=100`, `commits/${sourceCommit}/statuses?per_page=100`].includes(endpoint)) {
        const statuses = { sha: sourceCommit, total_count: reviewedHead === sourceCommit ? 1 : 0,
          statuses: reviewedHead === sourceCommit ? [structuredClone(canonical.status)] : [] };
        settings.mutateStatuses?.(statuses);
        return response(endpoint.includes('/statuses?') ? statuses.statuses : statuses);
      }
      if (channel === 'stable' && (endpoint === `git/commits/${sha}` || endpoint === `git/commits/${newerSha}`)) {
        return response(await trees(endpoint));
      }
      if (canonical.values.has(endpoint)) return response(canonical.values.get(endpoint));
      if (endpoint.startsWith('git/ref/tags/')) {
        return tag ? response({ object: { sha: tag, type: 'tag' } }) : response({}, 404);
      }
      if (method === 'GET') {
        if (channel === 'stable' && (endpoint === `git/commits/${sha}` || endpoint === `git/commits/${newerSha}` ||
          endpoint.includes('?recursive=1'))) {
          if (endpoint.includes('?recursive=1')) comparedTree = true;
          return response(await trees(endpoint));
        }
        const object = objects.get(endpoint.split('/').at(-1));
        assert.ok(object, `Missing fixture object: ${endpoint}`);
        return response(object);
      }

      const body = JSON.parse(options.body);
      if (endpoint === 'git/blobs') {
        ledgerWrites.push(JSON.parse(body.content));
        return response({ sha: put({ encoding: 'base64', content: Buffer.from(body.content).toString('base64') }) });
      }
      if (endpoint === 'git/trees') return response({ sha: put({ truncated: false, tree: body.tree }) });
      if (endpoint === 'git/commits') {
        return response({ sha: put({ tree: { sha: body.tree }, parents: body.parents.map(sha => ({ sha })) }) });
      }
      if (endpoint === 'git/refs/heads/release-ledger') {
        assert.equal(body.force, false);
        assert.equal(objects.get(body.sha).parents[0].sha, head);
        if (settings.advanceOnCas && !advanced) {
          appendSnapshot(settings.advanceOnCas);
          advanced = true;
          return response({}, 409);
        }
        head = body.sha;
        return response({});
      }
      if (endpoint === 'git/tags') return response({ sha: put({ object: { sha: body.object, type: body.type } }) });
      if (endpoint === 'git/refs') { tag = body.sha; return response({}); }
      throw new Error(`Unexpected request: ${method} ${endpoint}`);
    },
  };
}

test('executed admission pins transaction source while workflow control and canonical heads differ', async () => {
  const cwd = process.cwd();
  const root = resolve('.artifacts', `distinct-admission-identity-${process.pid}`);
  const savedFetch = globalThis.fetch;
  const savedOutput = process.env.GITHUB_OUTPUT;
  const savedRunnerTemp = process.env.RUNNER_TEMP;
  mkdirSync(root, { recursive: true });
  process.chdir(root);
  try {
    const runnerTemp = resolve('runner');
    const outputDirectory = resolve(runnerTemp, '_runner_file_commands');
    const outputPath = resolve(outputDirectory, 'set_output_12345678-1234-1234-1234-123456789abc');
    mkdirSync(outputDirectory, { recursive: true });
    writeFileSync(outputPath, '');
    process.env.RUNNER_TEMP = runnerTemp;
    process.env.GITHUB_OUTPUT = outputPath;
    const stable = authorizationFixture(state(), {
      channel: 'stable',
      sourceCommit: sha,
      observedBranchHead: sha,
      workflowCommit: workflowControlSha,
      currentBranchHead: sha,
    });
    globalThis.fetch = stable.fetch;
    const stableAdmission = await runFixtureControl('admit', stable);
    assert.equal(stable.env.GITHUB_SHA, workflowControlSha);
    assert.equal(stableAdmission.sourceCommit, sha);
    assert.equal(stableAdmission.workflowCommit, workflowControlSha);
    assert.equal(stableAdmission.channel, 'stable');
    assert.match(readFileSync(outputPath, 'utf8'), new RegExp(`^source_sha=${sha}$`, 'm'));

    const insider = authorizationFixture(state(), {
      sourceCommit: sha,
      observedBranchHead: newerSha,
      workflowCommit: workflowControlSha,
      currentBranchHead: currentCanonicalSha,
    });
    globalThis.fetch = insider.fetch;
    const insiderAdmission = await runFixtureControl('admit', insider);
    assert.equal(insiderAdmission.sourceCommit, sha);
    assert.equal(insiderAdmission.authorizedBranchHead, newerSha);
    assert.equal(insiderAdmission.workflowCommit, workflowControlSha);
    assert.equal(insiderAdmission.channel, 'insider');
  } finally {
    globalThis.fetch = savedFetch;
    if (savedOutput === undefined) delete process.env.GITHUB_OUTPUT;
    else process.env.GITHUB_OUTPUT = savedOutput;
    if (savedRunnerTemp === undefined) delete process.env.RUNNER_TEMP;
    else process.env.RUNNER_TEMP = savedRunnerTemp;
    process.chdir(cwd);
    rmSync(root, { recursive: true, force: true });
  }
});

test('executed admission and authorization use identical immutable transaction bindings', async () => {
  const cwd = process.cwd();
  const root = resolve('.artifacts', `transaction-binding-${process.pid}`);
  const savedFetch = globalThis.fetch;
  mkdirSync(root, { recursive: true });
  process.chdir(root);
  try {
    const fixture = authorizationFixture(state(), {
      sourceCommit: sha,
      observedBranchHead: newerSha,
      workflowCommit: workflowControlSha,
      currentBranchHead: currentCanonicalSha,
    });
    globalThis.fetch = fixture.fetch;
    const admitted = await runFixtureControl('admit', fixture);
    const authorized = await runFixtureControl('authorize', fixture);
    assert.deepEqual(
      Object.fromEntries(['sourceCommit', 'channel', 'sourceBranch', 'buildId', 'buildAttempt',
        'workflowIdentity', 'workflowCommit'].map(field => [field, admitted[field]])),
      Object.fromEntries(['sourceCommit', 'channel', 'sourceBranch', 'buildId', 'buildAttempt',
        'workflowIdentity', 'workflowCommit'].map(field => [field, authorized[field]])),
    );
    assert.equal(authorized.sourceCommit, sha);
    assert.equal(authorized.authorizedBranchHead, newerSha);
    assert.equal(authorized.workflowCommit, workflowControlSha);
  } finally {
    globalThis.fetch = savedFetch;
    process.chdir(cwd);
    rmSync(root, { recursive: true, force: true });
  }
});

for (const channel of ['stable', 'insider']) {
for (const approvalMode of ['single-maintainer', 'separation-of-duties']) {
test(`${channel} ${approvalMode} executes pin, admission, automatic qualification, authorization and consumption without canonical status`, async t => {
  const cwd = process.cwd();
  const root = resolve('.artifacts', `automatic-qualification-${process.pid}-${channel}-${approvalMode}`);
  const savedFetch = globalThis.fetch;
  const savedOutput = process.env.GITHUB_OUTPUT;
  mkdirSync(root, { recursive: true });
  process.chdir(root);
  delete process.env.GITHUB_OUTPUT;
  t.after(() => {
    globalThis.fetch = savedFetch;
    if (savedOutput !== undefined) process.env.GITHUB_OUTPUT = savedOutput;
    process.chdir(cwd);
    rmSync(root, { recursive: true, force: true });
  });
  const ledger = state();
  if (channel === 'stable') ledger.qualifications[sha] = hotfixQualification();
  const fixture = authorizationFixture(ledger, { channel, approvalMode, reviewedHead: '7'.repeat(40) });
  globalThis.fetch = fixture.fetch;
  const api = githubClient(fixture.env.GH_TOKEN);
  const transaction = await selectTransaction(fixture.env, api);
  fixture.env.RELEASE_TRANSACTION = JSON.stringify(transaction);
  const completedJobs = fixture.transactionJobs.splice(0);
  await runReleaseControl('admit', fixture.env);
  assert.ok(fixture.calls.every(call => call.method === 'GET'));
  assert.ok(fixture.calls.every(call => !/actions\/|\/status\?|\/check-runs\?/.test(call.endpoint)),
    'Admission cannot depend on evidence from its downstream jobs');
  await assert.rejects(runReleaseControl('authorize', fixture.env), /receipt unavailable/);
  await assert.rejects(qualifyTransaction(transaction, api), /qualification job/);
  assert.ok(fixture.calls.every(call => call.method === 'GET'));

  fixture.transactionJobs.push(...completedJobs);
  const receipt = await qualifyTransaction(transaction, api);
  mkdirSync(dirname(qualificationPath), { recursive: true });
  const saveReceipt = value => writeFileSync(qualificationPath, JSON.stringify(value));
  saveReceipt({ ...receipt, transaction: { ...transaction, sourceCommit: newerSha } });
  await assert.rejects(runReleaseControl('authorize', fixture.env), /another release transaction/);
  saveReceipt({ ...receipt, checkedAt: new Date(Date.now() - 60 * 60_000).toISOString(),
    expiresAt: new Date(Date.now() - 30 * 60_000).toISOString() });
  await assert.rejects(runReleaseControl('authorize', fixture.env), /expired/);
  saveReceipt(receipt);
  fixture.review.status.creator.login = 'attacker';
  await assert.rejects(runReleaseControl('authorize', fixture.env), /Untrusted source review/);
  fixture.review.status.creator.login = 'github-actions[bot]';
  fixture.transactionJobs[0].conclusion = 'failure';
  await assert.rejects(runReleaseControl('authorize', fixture.env), /qualification job/);
  fixture.transactionJobs[0].conclusion = 'success';
  assert.ok(fixture.calls.every(call => call.method === 'GET'));
  assert.equal(fixture.ledgerWrites.length, 0);
  assert.equal(existsSync(authorizationPath), false);

  const identity = await runReleaseControl('authorize', fixture.env);
  const firstWrite = fixture.calls.findIndex(call => call.method !== 'GET');
  assert.ok(firstWrite > 0);
  assert.ok(fixture.calls.slice(0, firstWrite).some(call => call.endpoint === 'actions/runs/31'),
    'Genuine reviewed-head provenance must precede the first ledger/tag mutation');
  assert.ok(fixture.calls.filter(call => call.method !== 'GET').every(call => call.publisher));
  const consumer = { ...fixture.env, RELEASE_PUBLIC_IDENTITY: JSON.stringify(publicAuthorization(identity)) };
  await runReleaseControl('consume', consumer, () => {});
  fixture.calls.length = 0;
  await runReleaseControl('preflight', consumer, () => {});
  assert.ok(fixture.calls.every(call => call.method === 'GET'));
  fixture.review.status.state = 'failure';
  await assert.rejects(runReleaseControl('preflight', consumer, () => {}), /Untrusted source review/);
  writeFileSync(privateSetPath, JSON.stringify(completeSet(identity)));
  await assert.rejects(runReleaseControl('advance', {
    ...consumer, RELEASE_EXPECTED_POINTER: '', RELEASE_VERIFIED_BRANCH_HEAD: sha,
  }, () => {}), /Untrusted source review/);
  assert.ok(fixture.calls.every(call => call.method === 'GET'),
    'Revoked evidence cannot reach publication preflight or pointer writes');
});
}
}

test('executed authority rejects initial and newly advanced stable floors before any source or publication write', async () => {
  const cwd = process.cwd();
  const root = resolve('.artifacts', `stable-floor-authority-${process.pid}`);
  const savedFetch = globalThis.fetch;
  const savedOutput = process.env.GITHUB_OUTPUT;
  mkdirSync(root, { recursive: true });
  process.chdir(root);
  delete process.env.GITHUB_OUTPUT;
  try {
    for (const channel of ['stable', 'insider']) {
      for (const kind of ['historical', 'pointer']) {
        const initial = publicLedger(stableFloorLedger(kind, '1.2.4'));
        for (const operation of ['admit', 'authorize']) {
          const fixture = authorizationFixture(initial, { channel });
          globalThis.fetch = fixture.fetch;
          await assert.rejects(runFixtureControl(operation, fixture), /effective stable floor/);
          assert.ok(fixture.calls.every(call => call.method === 'GET'));
          assert.equal(fixture.ledgerWrites.length, 0);
          assert.equal(existsSync(authorizationPath), false);
        }
      }
      for (const timing of ['advanceBeforeAllocation', 'advanceOnCas']) {
        const initial = stableFloorLedger('historical', '1.2.2');
        const stable = reserve(initial, stableAdmission('1.2.4', { buildId: '43' }),
          created, undefined, hotfixQualification()).record;
        const advanced = structuredClone(initial);
        advance(advanced, stable, completeSet(stable), sha, '');
        const fixture = authorizationFixture(publicLedger(initial), { channel, [timing]: publicLedger(advanced) });
        globalThis.fetch = fixture.fetch;
        await assert.rejects(runFixtureControl('authorize', fixture),
          channel === 'insider' || timing === 'advanceBeforeAllocation'
            ? /effective stable floor/
            : /Unadvanced stable reservation blocks a new stable allocation/);
        const writes = fixture.calls.filter(call => call.method !== 'GET');
        const endpoints = writes.map(call => call.endpoint);
        assert.ok(endpoints.length === 0 || JSON.stringify(endpoints) === JSON.stringify([
          'git/blobs', 'git/trees', 'git/commits', 'git/refs/heads/release-ledger',
        ]), 'Only a losing ledger CAS may be attempted; no retry or source/release/asset writes');
        if (endpoints.length === 0) assert.equal(existsSync(authorizationPath), false);
        const current = await gitLedger(githubClient(fixture.env.RELEASE_PUBLISHER_TOKEN), anchor).read();
        assert.ok(
          [initial, advanced].some(expected =>
            JSON.stringify(current.state) === JSON.stringify(publicLedger(expected))),
          'The rejected reservation may observe only the original or concurrently advanced ledger state');
        assert.equal(current.state.counter, '0');
        rmSync('.artifacts', { recursive: true, force: true });
      }
    }
  } finally {
    globalThis.fetch = savedFetch;
    if (savedOutput !== undefined) process.env.GITHUB_OUTPUT = savedOutput;
    process.chdir(cwd);
    rmSync(root, { recursive: true, force: true });
  }
});

test('executed authority preserves existing exact reservations after stable advances above their base', async () => {
  const ledger = stableFloorLedger('historical', '1.2.2');
  const insider = record(ledger);
  const stable = reserve(ledger, stableAdmission(), created, undefined, hotfixQualification()).record;
  advance(ledger, stable, completeSet(stable), sha, '');
  const stablePointer = signedReleasePointer(stable, signedManifest(stable, completeSet(stable)));
  const newer = reserve(ledger, stableAdmission('1.2.4', { buildId: '43' }),
    created, undefined, hotfixQualification()).record;
  advance(ledger, newer, completeSet(newer), sha, stablePointer.manifestEnvelopeSha256);
  const cwd = process.cwd();
  const root = resolve('.artifacts', `stable-floor-retry-${process.pid}`);
  const savedFetch = globalThis.fetch;
  const savedOutput = process.env.GITHUB_OUTPUT;
  mkdirSync(root, { recursive: true });
  process.chdir(root);
  delete process.env.GITHUB_OUTPUT;
  try {
    for (const identity of [insider, stable]) {
      writeAuthorization(identity);
      const original = readFileSync(authorizationPath, 'utf8');
      const fixture = authorizationFixture(publicLedger(ledger), {
        channel: identity.channel,
        workflowCommit: identity.workflowCommit,
      });
      globalThis.fetch = fixture.fetch;
      await runReleaseControl('admit', fixture.env);
      await runFixtureControl('authorize', fixture);
      assert.equal(readFileSync(authorizationPath, 'utf8'), original);
      for (const snapshot of fixture.ledgerWrites) {
        assert.equal(snapshot.counter, ledger.counter);
        assert.deepEqual(Object.keys(snapshot.reservations), Object.keys(ledger.reservations));
      }
    }
  } finally {
    globalThis.fetch = savedFetch;
    if (savedOutput !== undefined) process.env.GITHUB_OUTPUT = savedOutput;
    process.chdir(cwd);
    rmSync(root, { recursive: true, force: true });
  }
});

test('executed admission and authorization reject multi-commit evidence rewrites before any writes', async () => {
  const seed = state();
  record(seed, { buildId: '41' });
  seed.qualifications[newerSha] = { ...hotfixQualification(), sourceCommit: newerSha };
  const previous = publicLedger(seed);
  const changed = structuredClone(previous);
  changed.qualifications[newerSha].reasonSha256 = hotfixReasonDigest('Different rationale retained by the fast-forward head.');
  validateLedger(changed, anchor);
  const savedFetch = globalThis.fetch;
  try {
    for (const operation of ['admit', 'authorize']) {
      const fixture = authorizationFixture(previous, { history: [previous, changed, changed] });
      globalThis.fetch = fixture.fetch;
      await assert.rejects(runFixtureControl(operation, fixture), /immutable qualification/);
      assert.equal(fixture.calls.filter(call => call.method === 'POST' || call.method === 'PATCH').length, 0);
      assert.equal(fixture.ledgerWrites.length, 0);
    }
  } finally {
    globalThis.fetch = savedFetch;
  }
});

test('executed admission and authorization reject historical floor and unbound counter changes before writes', async () => {
  const seed = state();
  record(seed, { buildId: '41' });
  seed.lastHistoricalStable = '1.2.2';
  const previous = publicLedger(seed);
  const savedFetch = globalThis.fetch;
  try {
    for (const [name, mutate] of [
      ['delete floor', next => { delete next.lastHistoricalStable; }],
      ['lower floor', next => { next.lastHistoricalStable = '1.2.1'; }],
      ['replace floor', next => { next.lastHistoricalStable = '1.2.3'; }],
      ['unbound increment', next => { next.counter = '2'; }],
      ['unbound jump', next => { next.counter = '3'; }],
      ['unbound large jump', next => { next.counter = '9007199254740993'; }],
    ]) {
      const changed = structuredClone(previous);
      mutate(changed);
      validateLedger(changed, anchor);
      for (const [historyName, history] of [
        ['retained', [previous, changed, changed]],
        ['restored', [previous, changed, previous]],
      ]) {
        for (const operation of ['admit', 'authorize']) {
          const fixture = authorizationFixture(previous, { history });
          globalThis.fetch = fixture.fetch;
          await assert.rejects(runFixtureControl(operation, fixture), ReleasePolicyError,
            `${name}, ${historyName}: ${operation}`);
          assert.equal(fixture.calls.filter(call => call.method === 'POST' || call.method === 'PATCH').length, 0);
          assert.equal(fixture.ledgerWrites.length, 0);
        }
      }
    }
  } finally {
    globalThis.fetch = savedFetch;
  }
});

test('stable authority verifies tree evidence and rechecks main HEAD before any Git write', async () => {
  const ledger = state();
  const insider = record(ledger);
  const set = completeSet(insider);
  advance(ledger, insider, set, sha, '');
  ledger.qualifications[newerSha] = promotionQualification(insider, set);
  const cwd = process.cwd();
  const root = resolve('.artifacts', `stable-authority-${process.pid}`);
  const originalFetch = globalThis.fetch;
  mkdirSync(root, { recursive: true });
  process.chdir(root);
  try {
    for (const options of [
      { treeOptions: { mutate: entries => { entries[1].sha = '1'.repeat(40); } } },
      { treeOptions: { truncated: true } }, { headDriftAfterTree: true }, {},
    ]) {
      const fixture = authorizationFixture(publicLedger(ledger), { channel: 'stable', sourceCommit: newerSha, ...options });
      globalThis.fetch = fixture.fetch;
      if (Object.keys(options).length) {
        await assert.rejects(runFixtureControl('authorize', fixture), ReleasePolicyError);
        assert.ok(fixture.calls.every(call => call.method === 'GET'));
        assert.equal(existsSync(authorizationPath), false);
      } else {
        await runFixtureControl('authorize', fixture);
        const firstWrite = fixture.calls.findIndex(call => call.method !== 'GET');
        const preflight = fixture.calls.slice(0, firstWrite);
        assert.equal(preflight.filter(call => call.endpoint.includes('?recursive=1')).length, 2);
        assert.ok(preflight.findLastIndex(call => call.endpoint === 'git/ref/heads/main') >
          preflight.findLastIndex(call => call.endpoint.includes('?recursive=1')));
        const identity = JSON.parse(readFileSync(authorizationPath, 'utf8'));
        assert.deepEqual(identity.qualification, ledger.qualifications[newerSha]);
      }
    }
  } finally {
    globalThis.fetch = originalFetch;
    process.chdir(cwd);
    rmSync(root, { recursive: true, force: true });
  }
});

test('every ledger write path removes unknown top-level seed fields without changing immutable references', async () => {
  const seed = state();
  seed.lastHistoricalStable = '1.2.2';
  seed.qualifications[sha] = hotfixQualification();
  const privateSeed = {
    reviewers: [{ id: 7, login: 'private-value' }], publisherAppId: 'private-value',
    protection: { rulesets: [{ id: 7 }] }, ownerNotes: 'private-value',
    futurePrivate: { nested: 'private-value' },
  };
  const fixture = authorizationFixture(seed);
  const originalFetch = globalThis.fetch;
  globalThis.fetch = fixture.fetch;
  try {
    const store = gitLedger(githubClient(fixture.env.RELEASE_PUBLISHER_TOKEN), anchor);
    // Reintroduce contamination at each transaction, including unchanged retries.
    const write = mutate => transact(store, current => {
      Object.assign(current, privateSeed);
      return mutate(current);
    });
    const identity = await write(current => reserve(current, admission(), created).record);
    const retry = await write(current => reserve(current, admission(), created).record);
    assert.equal(retry.identitySha256, hash(identity));
    await ensureSourceTag(githubClient(fixture.env.RELEASE_PUBLISHER_TOKEN), store, identity,
      (_store, mutate) => write(mutate));
    const set = completeSet(identity);
    await write(current => advance(current, identity, set, sha, ''));
    await write(current => advance(current, identity, set, sha, publicSetHash(set)));
    assert.equal(fixture.ledgerWrites.length, 6, 'Allocation, retry, tag object, tag publication, set and set retry');
    for (const persisted of fixture.ledgerWrites) {
      assert.deepEqual(Object.keys(persisted).sort(), [...publicLedgerFields].sort());
      assert.doesNotMatch(JSON.stringify(persisted), /private-value|reviewers|publisherAppId|protection|ownerNotes|futurePrivate/);
      assert.deepEqual(persisted.qualifications[sha], hotfixQualification());
      assert.equal(persisted.lastHistoricalStable, '1.2.2');
      assert.equal(persisted.counter, '1');
      assert.equal(persisted.identities[identity.canonicalVersion], identity.allocationKey);
      assert.equal(persisted.reservations[identity.allocationKey].identitySha256, hash(identity));
      assert.deepEqual(publicLedger(persisted), persisted);
      validateLedger(persisted, anchor);
    }
    const persisted = (await store.read()).state; // Also verifies adjacent-commit CAS continuity.
    const entry = persisted.reservations[identity.allocationKey];
    assert.equal(entry.setHash, publicSetHash(set));
    assert.equal(entry.set.identity.identitySha256, hash(identity));
    assert.deepEqual(entry.set.images, set.images);
    assert.equal(entry.tagPublished, true);
    assert.equal(persisted.pointers.insider.manifestEnvelopeSha256,
      signedReleasePointer(identity, signedManifest(identity, set)).manifestEnvelopeSha256);
    assert.deepEqual(fixture.ledgerWrites[4], fixture.ledgerWrites[5]);
    verifyConsumer(identity, entry, context(), entry.identitySha256);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test('every transaction rejects private qualification seed fields before any Git write', async () => {
  const seed = state();
  const identity = record(seed);
  const set = completeSet(identity);
  advance(seed, identity, set, sha, '');
  const nextIdentity = record(seed, { buildId: '43' });
  const clean = publicLedger(seed);
  const operations = [
    current => reserve(current, admission({ buildId: '44', buildAttempt: '3' }), created),
    current => reserve(current, admission(), created),
    current => { current.reservations[identity.allocationKey].tagObject = newerSha; },
    current => { Object.assign(current.reservations[identity.allocationKey], { tagObject: newerSha, tagPublished: true }); },
    current => advance(current, nextIdentity, completeSet(nextIdentity), sha, publicSetHash(set)),
    current => advance(current, identity, set, sha, publicSetHash(set)),
  ];
  const promotion = promotionQualification(identity, set);
  const poisonedQualifications = [
    ...['reviewers', 'reviewerLogin', 'reviewerId', 'publisherAppId', 'rawPolicy', 'hotfixReason', 'ownerNotes', 'futurePrivate']
      .map(field => ({ ...hotfixQualification(), [field]: { secret: 'private-value' } })),
    ...['reviewerLogin', 'publisherAppId', 'rawPolicy', 'futurePrivate']
      .map(field => ({ ...promotion, promotionOrigin: { ...promotion.promotionOrigin, [field]: 'private-value' } })),
    { ...hotfixQualification(), reviewed: { login: 'private-value' } },
    { ...hotfixQualification(), sourceCommit: { id: 'private-value' } },
  ];
  for (const qualification of poisonedQualifications) {
    for (const operation of operations) {
      const contaminated = structuredClone(clean);
      contaminated.qualifications[qualification.sourceCommit === newerSha ? newerSha : sha] = qualification;
      let writes = 0;
      const adapter = gitLedger(async () => { writes++; throw new Error('Unexpected Git write'); }, anchor);
      const store = {
        read: async () => ({ revision: newerSha, state: structuredClone(contaminated) }),
        compareAndSet: adapter.compareAndSet,
      };
      await assert.rejects(transact(store, operation), /public ledger .*qualification/);
      assert.equal(writes, 0, 'Reject before creating even an unreachable public Git blob');
    }
  }
  const qualified = structuredClone(clean);
  qualified.qualifications[newerSha] = promotion;
  assert.deepEqual(publicLedger(qualified).qualifications[newerSha], promotion);
});

function publicSetPoisons(identity) {
  const invalidValues = [undefined, JSON.parse('null'), [], { private: 'private-value' }, 'private-value'];
  const mutations = [
    ...invalidValues.map(value => set => { set.images = value; }),
    set => { set.images.private = { private: 'private-value' }; },
    set => {
      set.images['api,frontend'] = set.images.api;
      delete set.images.api;
      delete set.images.frontend;
    },
    set => { set.schema = 2; },
    set => { set.managedEligible = true; },
    set => { set.identity.sourceCommit = newerSha; },
    set => { set.identity.identitySha256 = 'f'.repeat(64); },
    set => { set.identity.futurePrivate = { private: 'private-value' }; },
  ];
  const invalidDigests = [...invalidValues, 'sha256:bad', `sha256:${'A'.repeat(64)}`,
    `sha256:${'a'.repeat(63)}`, `sha256:${'a'.repeat(65)}`, `sha256:${'a'.repeat(64)}\n`];
  for (const [name, platforms] of Object.entries(components)) {
    mutations.push(set => { delete set.images[name]; });
    mutations.push(...invalidValues.map(value => set => { set.images[name] = value; }));
    mutations.push(...invalidDigests.map(value => set => { set.images[name].digest = value; }));
    mutations.push(...invalidValues.map(value => set => { set.images[name].platforms = value; }));
    mutations.push(set => { set.images[name].platforms['private/platform'] = { private: 'private-value' }; });
    for (const platform of platforms) {
      const target = set => set.images[name].platforms[platform];
      mutations.push(set => { delete set.images[name].platforms[platform]; });
      mutations.push(...invalidValues.map(value => set => { set.images[name].platforms[platform] = value; }));
      mutations.push(...invalidDigests.map(value => set => { target(set).digest = value; }));
      mutations.push(...invalidValues.map(value => set => { target(set).labels = value; }));
      for (const label of Object.keys(identityLabels(identity))) {
        mutations.push(set => { delete target(set).labels[label]; });
        mutations.push(...invalidValues.map(value => set => { target(set).labels[label] = value; }));
      }
    }
  }
  return mutations;
}

function addUnknownSetFields(set) {
  set.futurePrivate = { nested: 'private-value' };
  for (const image of Object.values(set.images)) {
    image.futurePrivate = { nested: 'private-value' };
    for (const platform of Object.values(image.platforms)) {
      platform.futurePrivate = { nested: 'private-value' };
      platform.labels.futurePrivate = { nested: 'private-value' };
    }
  }
}

test('public complete sets are closed, canonical and idempotent', () => {
  const seed = state();
  const identity = record(seed);
  const set = completeSet(identity);
  const projected = writePublicSet(identity, set);
  assert.deepEqual(projected.identity, publicAuthorization(identity));
  assert.deepEqual(projected.images, set.images);
  addUnknownSetFields(set);
  const before = JSON.stringify(set);
  assert.throws(() => writePublicSet(identity, set), /public set/i);
  assert.equal(JSON.stringify(set), before, 'Projection must not mutate hashed input');
  assert.deepEqual(writePublicSet(identity, projected), projected);
  assert.deepEqual(writePublicSet(projected.identity, projected), projected);
  advance(seed, identity, completeSet(identity), sha, '');
  const ledger = publicLedger(seed);
  const entry = ledger.reservations[identity.allocationKey];
  assert.deepEqual(writePublicSet(entry.record, entry.set), projected);
  assert.deepEqual(publicLedger(ledger), ledger);
  assert.equal(entry.identitySha256, hash(identity));
  assert.equal(entry.setHash, publicSetHash(completeSet(identity)));
  assert.equal(ledger.pointers.insider.manifestEnvelopeSha256,
    signedReleasePointer(identity, signedManifest(identity, completeSet(identity))).manifestEnvelopeSha256);
  for (const poison of publicSetPoisons(identity)) {
    for (const original of [completeSet(identity), projected]) {
      const changed = structuredClone(original);
      poison(changed);
      assert.throws(() => writePublicSet(identity, changed), /public set/i);
    }
  }
  for (const invalid of ['', 'f'.repeat(63), `${'f'.repeat(64)}\n`, {}, [], JSON.parse('null')]) {
    assert.throws(() => writePublicSet(identity, projected, invalid), /identity hash/);
  }
});

test('every ledger write path rejects poisoned stored public sets before Git blob creation', async t => {
  const seed = state();
  const identity = record(seed);
  const set = completeSet(identity);
  advance(seed, identity, set, sha, '');
  const nextIdentity = record(seed, { buildId: '43' });
  const clean = publicLedger(seed);
  const operations = [
    current => reserve(current, admission({ buildId: '44', buildAttempt: '3' }), created),
    current => reserve(current, admission(), created),
    current => { current.reservations[identity.allocationKey].tagObject = newerSha; },
    current => { Object.assign(current.reservations[identity.allocationKey], { tagObject: newerSha, tagPublished: true }); },
    current => advance(current, nextIdentity, completeSet(nextIdentity), sha, publicSetHash(set)),
    current => advance(current, identity, set, sha, publicSetHash(set)),
  ];
  let rejected = 0;
  for (const poison of publicSetPoisons(identity)) {
    for (const operation of operations) {
      const contaminated = structuredClone(clean);
      poison(contaminated.reservations[identity.allocationKey].set);
      let calls = 0;
      const adapter = gitLedger(async () => { calls++; throw new Error('Unexpected Git call'); }, anchor);
      const store = {
        read: async () => ({ revision: sha, state: structuredClone(contaminated) }),
        compareAndSet: adapter.compareAndSet,
      };
      await assert.rejects(transact(store, operation), /public set/i);
      assert.equal(calls, 0, 'Reject before even an unreachable Git blob or parent lookup');
      rejected++;
    }
  }
  for (const invalid of [false, JSON.parse('null'), [], 'private-value']) {
    const changed = structuredClone(clean);
    changed.reservations[identity.allocationKey].set = invalid;
    let calls = 0;
    await assert.rejects(gitLedger(async () => { calls++; }, anchor).compareAndSet(sha, changed), /public set/i);
    assert.equal(calls, 0);
  }
  for (const operation of operations) {
    const contaminated = structuredClone(clean);
    addUnknownSetFields(contaminated.reservations[identity.allocationKey].set);
    let calls = 0;
    const adapter = gitLedger(async (endpoint, method, body) => {
      calls++;
      if (endpoint === `git/commits/${sha}`) return { tree: { sha: anchor } };
      if (endpoint === 'git/blobs') {
        return { sha: anchor };
      }
      if (endpoint === 'git/trees') return { sha: anchor };
      if (endpoint === 'git/commits') {
        assert.deepEqual(body.parents, [sha]);
        return { sha: newerSha };
      }
      assert.equal(endpoint, 'git/refs/heads/release-ledger');
      assert.deepEqual(body, { sha: newerSha, force: false });
      return {};
    }, anchor);
    await assert.rejects(transact({
      read: async () => ({ revision: sha, state: contaminated }), compareAndSet: adapter.compareAndSet,
    }, operation), /public set/i);
    assert.equal(calls, 0, 'Unknown persisted release-set data must block before Git writes');
  }
  t.diagnostic(`${rejected} poisoned-set/transaction combinations rejected before any Git call`);
});

test('public ledger schema rejects malformed maps and typed references; stable owner approval remains mandatory', async () => {
  for (const field of ['reservations', 'identities', 'pointers', 'stages', 'qualifications']) {
    const malformed = state();
    malformed[field] = [{ ownerNotes: 'private-value' }];
    assert.throws(() => publicLedger(malformed));
  }
  const stable = admit(context({ channel: 'stable' }), sha, 'v1.2.3');
  for (const field of ['reviewed', 'tests', 'compatibility', 'migrations', 'recovery', 'reasonSha256']) {
    for (const invalid of [false, 'true', { private: 'private-value' }, undefined]) {
      const ledger = state();
      ledger.qualifications[sha] = { ...hotfixQualification(), [field]: invalid };
      assert.throws(() => publicLedger(ledger));
      assert.throws(() => reserve(ledger, stable, created));
      assert.deepEqual(ledger.reservations, {});
    }
  }
  const ledger = state();
  const identity = record(ledger);
  for (const field of ['tagObject', 'tagPublished', 'setHash']) {
    const changed = structuredClone(ledger);
    changed.reservations[identity.allocationKey][field] = { private: 'private-value' };
    assert.throws(() => publicLedger(changed), /public ledger/);
  }
  ledger.qualifications[sha] = hotfixQualification();
  const normalized = publicLedger(ledger);
  const qualification = await verifyStableQualification(() => assert.fail('No hotfix tree lookup'), normalized, stable);
  assert.equal(reserve(normalized, stable, created, undefined, qualification).record.qualification.mode, 'hotfix');
  assert.throws(() => publicLedger({ ...ledger, lastHistoricalStable: '1.2.3-insider.1' }), /stable floor/);
});

for (const approvalMode of ['single-maintainer', 'separation-of-duties']) {
test(`${approvalMode} control flow keeps github.token read-only and requires App verification before any writes`, async () => {
  const fixture = authorizationFixture(state(), { approvalMode });
  const originalFetch = globalThis.fetch;
  const cwd = process.cwd();
  const previousOutput = process.env.GITHUB_OUTPUT;
  const previousRunner = process.env.RUNNER_TEMP;
  const root = resolve('.artifacts', `authorization-${process.pid}`);
  mkdirSync(root, { recursive: true });
  globalThis.fetch = fixture.fetch;
  process.chdir(root);
  process.env.RUNNER_TEMP = resolve('runner');
  mkdirSync(resolve('runner', '_runner_file_commands'), { recursive: true });
  process.env.GITHUB_OUTPUT = resolve('runner', '_runner_file_commands',
    'set_output_00000000-0000-0000-0000-000000000000');
  writeFileSync(process.env.GITHUB_OUTPUT, '');
  const verify = (file, args) => {
    assert.equal(file, 'cosign');
    assert.deepEqual(args, ['verify-blob', '--bundle', authorizationBundle,
      '--certificate-identity', publisherWorkflowIdentity,
      '--certificate-oidc-issuer', 'https://token.actions.githubusercontent.com', authorizationPath]);
  };
  try {
    await runReleaseControl('admit', { ...fixture.env, RELEASE_PUBLISHER_TOKEN: undefined });
    assert.match(readFileSync(process.env.GITHUB_OUTPUT, 'utf8'), new RegExp(`^approval_mode=${approvalMode}$`, 'm'));
    assert.ok(fixture.calls.length > 0);
    assert.ok(fixture.calls.every(call => !call.admin && !call.publisher && call.method === 'GET'));
    fixture.calls.length = 0;
    for (const operation of ['authorize', 'advance']) {
      for (const token of [undefined, fixture.env.GH_TOKEN]) {
        await assert.rejects(runFixtureControl(operation, fixture, {
          ...fixture.env, RELEASE_PUBLISHER_TOKEN: token,
        }),
          /Protected publisher App token required/);
      }
    }
    assert.equal(fixture.calls.length, 0, 'Missing App credential must fail before API calls');
    await assert.rejects(runFixtureControl('authorize', fixture, {
      ...fixture.env,
      RELEASE_SOURCE_COMMIT: newerSha,
    }), /source identity does not match/);
    assert.equal(fixture.calls.length, 0,
      'Mismatched redundant source identity must fail before API calls');
    const denied = { ...fixture.env, RELEASE_PUBLISHER_TOKEN: 'not-authorized-fixture' };
    await assert.rejects(runFixtureControl('authorize', fixture, denied), /HTTP 403/);
    assert.ok(fixture.calls.every(call => call.method === 'GET'), '403 must not allocate or tag');
    fixture.calls.length = 0;
    await runFixtureControl('authorize', fixture);
    const identity = JSON.parse(readFileSync(authorizationPath, 'utf8'));
    verifyProtectionEvidence(identity.protection, 'insider');
    assert.equal(identity.protection.approvalMode, approvalMode);
    const firstWrite = fixture.calls.findIndex(call => call.method !== 'GET');
    const adminCalls = fixture.calls.filter(call => call.admin);
    assert.ok(adminCalls.length >= 8 && adminCalls.every(call => call.publisher));
    assert.ok(fixture.calls.slice(firstWrite).every(call => !call.admin), 'Protection must precede allocation');
    const signedBytes = readFileSync(authorizationPath, 'utf8');
    await runFixtureControl('authorize', fixture);
    assert.equal(readFileSync(authorizationPath, 'utf8'), signedBytes, 'Retry retains original evidence bytes');
    const changedMode = approvalMode === 'single-maintainer' ? 'separation-of-duties' : 'single-maintainer';
    const originalRules = structuredClone(fixture.environment.protection_rules);
    const originalBranchPolicy = structuredClone(fixture.branchRules[2].parameters);
    fixture.environment.protection_rules = protectionFixture('insider', changedMode).environment.protection_rules;
    fixture.branchRules[2].parameters = protectionFixture('insider', changedMode).branchRules[2].parameters;
    fixture.calls.length = 0;
    await assert.rejects(runFixtureControl('authorize', fixture, {
      ...fixture.env,
      RELEASE_APPROVAL_MODE: changedMode,
      RELEASE_ADMITTED_APPROVAL_MODE: changedMode,
    }),
      /Approval mode changed after transaction selection/);
    assert.ok(fixture.calls.every(call => call.method === 'GET'));
    fixture.environment.protection_rules = originalRules;
    fixture.branchRules[2].parameters = originalBranchPolicy;
    fixture.calls.length = 0;
    const consumer = {
      ...fixture.env,
      RELEASE_PUBLISHER_TOKEN: undefined,
      RELEASE_PUBLIC_IDENTITY: JSON.stringify(publicAuthorization(identity)),
      RELEASE_TRANSACTION: JSON.stringify(releaseTransaction(identity)),
      RELEASE_SOURCE_COMMIT: identity.sourceCommit,
    };
    await runReleaseControl('consume', consumer, verify);
    const frontendOutput = readFileSync(process.env.GITHUB_OUTPUT, 'utf8')
      .split('\n').find(line => line.startsWith('frontend_identity='));
    assert.equal(frontendOutput, `frontend_identity=${buildMetadata(identity).frontendIdentity}`);
    assert.ok(fixture.calls.every(call => !call.admin && !call.publisher && call.method === 'GET'));
    assert.match(readFileSync('src/ReleaseIdentity.props', 'utf8'), /1\.2\.3-insider\.1/);
    assert.equal(readFileSync(authorizationPath, 'utf8'), signedBytes,
      'Public projection must preserve the signed private record bytes');
    const publicIdentity = readFileSync('src/Web/ReactApp/public/release-identity.json', 'utf8');
    assert.doesNotMatch(publicIdentity, /protection|rulesets|environment|reviewers|publisherAppId/);
    assert.equal(JSON.parse(publicIdentity).identitySha256, hash(identity));
    fixture.calls.length = 0;
    writeFileSync(privateSetPath, JSON.stringify(completeSet(identity)));
    const manifest = signedManifest(identity, completeSet(identity));
    writeFileSync(manifestPath, manifest.serializedManifest);
    writeFileSync(manifestEnvelopePath, manifest.serializedEnvelope);
    fixture.calls.length = 0;
    await runReleaseControl('preflight', {
      ...fixture.env,
      RELEASE_PUBLIC_IDENTITY: consumer.RELEASE_PUBLIC_IDENTITY,
      RELEASE_TRANSACTION: consumer.RELEASE_TRANSACTION,
      RELEASE_SOURCE_COMMIT: identity.sourceCommit,
    }, verify);
    const preflightOutputs = readFileSync(process.env.GITHUB_OUTPUT, 'utf8');
    assert.match(preflightOutputs, new RegExp(`^verified_branch_head=${identity.sourceCommit}$`, 'm'));
    assert.match(preflightOutputs, /^expected_pointer=$/m);
    assert.ok(fixture.calls.every(call => call.method === 'GET'),
      'Publication preflight must remain read-only');
    fixture.calls.length = 0;
    await runReleaseControl('advance', {
      ...fixture.env,
      RELEASE_PUBLIC_IDENTITY: consumer.RELEASE_PUBLIC_IDENTITY,
      RELEASE_TRANSACTION: consumer.RELEASE_TRANSACTION,
      RELEASE_SOURCE_COMMIT: identity.sourceCommit,
      RELEASE_VERIFIED_BRANCH_HEAD: identity.sourceCommit,
      RELEASE_EXPECTED_POINTER: '',
    }, verify);
    assert.ok(fixture.calls.some(call => call.method === 'PATCH'));
    assert.ok(fixture.calls.filter(call => call.admin || call.method !== 'GET').every(call => call.publisher),
      'Only the publisher App may read protected policy or write; github.token revalidates public evidence');
    fixture.calls.length = 0;
    const changed = structuredClone(identity);
    changed.protection.claims.exclusiveApprovedPublisher = false;
    writeFileSync(authorizationPath, JSON.stringify(changed));
    await assert.rejects(runReleaseControl('consume', consumer, verify), /differs from public identity/);
    writeFileSync(authorizationPath, signedBytes);
    await assert.doesNotReject(runReleaseControl('consume', { ...consumer, GITHUB_RUN_ATTEMPT: '2' }, verify));
    fixture.deleteTag();
    await assert.rejects(runReleaseControl('consume', consumer, verify), /tag missing/);
    assert.ok(fixture.calls.every(call => call.method === 'GET'));
    const outputs = readFileSync(process.env.GITHUB_OUTPUT, 'utf8');
    assert.doesNotMatch(outputs, /protection|rulesets|environments|reviewers|publisherAppId|actor_id/);
    assert.ok(outputs.includes(`identity_hash=${hash(identity)}`));
    const projectedOutput = JSON.parse(outputs.split('\n').find(line => line.startsWith('public_identity=')).split('=').slice(1).join('='));
    assert.deepEqual(projectedOutput, publicAuthorization(identity));
    for (const file of readdirSync(root, { recursive: true, withFileTypes: true }).filter(entry => entry.isFile())) {
      assert.doesNotMatch(readFileSync(resolve(file.parentPath, file.name), 'utf8'), privateFields);
    }
  } finally {
    if (previousOutput === undefined) delete process.env.GITHUB_OUTPUT;
    else process.env.GITHUB_OUTPUT = previousOutput;
    if (previousRunner === undefined) delete process.env.RUNNER_TEMP;
    else process.env.RUNNER_TEMP = previousRunner;
    process.chdir(cwd);
    globalThis.fetch = originalFetch;
    rmSync(root, { recursive: true, force: true });
  }
});
}

test('publication preflight accepts canonical ancestry and rejects branch drift before writes', async () => {
  const fixture = authorizationFixture();
  const originalFetch = globalThis.fetch;
  const cwd = process.cwd();
  const previousOutput = process.env.GITHUB_OUTPUT;
  const previousRunner = process.env.RUNNER_TEMP;
  const root = resolve('.artifacts', `preflight-${process.pid}`);
  mkdirSync(resolve(root, 'runner', '_runner_file_commands'), { recursive: true });
  process.chdir(root);
  process.env.RUNNER_TEMP = resolve('runner');
  process.env.GITHUB_OUTPUT = resolve('runner', '_runner_file_commands',
    'set_output_00000000-0000-0000-0000-000000000000');
  writeFileSync(process.env.GITHUB_OUTPUT, '');
  globalThis.fetch = fixture.fetch;
  try {
    await runFixtureControl('authorize', fixture);
    const identity = JSON.parse(readFileSync(authorizationPath, 'utf8'));
    writeFileSync(privateSetPath, JSON.stringify(completeSet(identity)));
    const env = {
      ...fixture.env,
      RELEASE_PUBLIC_IDENTITY: JSON.stringify(publicAuthorization(identity)),
      RELEASE_TRANSACTION: JSON.stringify(releaseTransaction(identity)),
      RELEASE_SOURCE_COMMIT: identity.sourceCommit,
    };
    fixture.calls.length = 0;
    fixture.setCanonicalHead(newerSha);
    await runReleaseControl('preflight', env, () => {});
    const outputs = readFileSync(process.env.GITHUB_OUTPUT, 'utf8');
    assert.match(outputs, new RegExp(`^verified_branch_head=${newerSha}$`, 'm'));
    assert.match(outputs, /^expected_pointer=$/m);
    assert.ok(fixture.calls.every(call => call.method === 'GET'));
    fixture.calls.length = 0;
    fixture.setCanonicalHead('f'.repeat(40));
    fixture.setCanonicalComparison({
      status: 'diverged',
      merge_base_commit: { sha: anchor },
    });
    await assert.rejects(runReleaseControl('preflight', env, () => {}),
      /trusted canonical branch history/);
    assert.ok(fixture.calls.every(call => call.method === 'GET'));
  } finally {
    globalThis.fetch = originalFetch;
    if (previousOutput === undefined) delete process.env.GITHUB_OUTPUT;
    else process.env.GITHUB_OUTPUT = previousOutput;
    if (previousRunner === undefined) delete process.env.RUNNER_TEMP;
    else process.env.RUNNER_TEMP = previousRunner;
    process.chdir(cwd);
    rmSync(root, { recursive: true, force: true });
  }
});

test('missing, malformed and weakened signed protection evidence always fails closed', async () => {
  const evidence = await verifyProtection(protectionFixture().api, 'insider', '123', 'separation-of-duties');
  for (const mutate of [
    item => { item.schema = 0; }, item => { item.repository = 'fork/repo'; },
    item => { item.verifiedAt = 'invalid'; }, item => { item.branchRules = []; },
    item => { item.claims.nonSelfApprovalRequired = false; }, item => { delete item.claims.canonicalTagsImmutable; },
    item => { item.policyProfile = 'unknown/v1'; },
    item => { item.policyDigest = '0'.repeat(64); },
    item => { item.verifiedAt = '2026-09-12T20:00:01.000Z'; },
    item => { item.claims.actor_id = 456; },
  ]) {
    const invalid = structuredClone(evidence);
    mutate(invalid);
    assert.throws(() => verifyProtectionEvidence(invalid, 'insider'));
  }
  assert.throws(() => verifyProtectionEvidence(undefined, 'insider'), /Missing/);
  const ledger = state();
  const identity = record(ledger);
  delete identity.protection;
  const fixture = authorizationFixture(ledger, { workflowCommit: identity.workflowCommit });
  const originalFetch = globalThis.fetch;
  const cwd = process.cwd();
  const root = resolve('.artifacts', `invalid-authorization-${process.pid}`);
  mkdirSync(root, { recursive: true });
  process.chdir(root);
  mkdirSync(resolve(authorizationPath, '..'), { recursive: true });
  writeFileSync(authorizationPath, JSON.stringify(identity));
  globalThis.fetch = fixture.fetch;
  try {
    await assert.rejects(runReleaseControl('consume', {
      ...fixture.env,
      RELEASE_PUBLIC_IDENTITY: JSON.stringify(publicAuthorization(identity)),
      RELEASE_PUBLISHER_TOKEN: undefined,
      RELEASE_TRANSACTION: JSON.stringify(releaseTransaction({ ...identity, protection: fixtureProtection('insider') })),
      RELEASE_SOURCE_COMMIT: identity.sourceCommit,
    }, () => {}), /authorization fields/);
    assert.ok(fixture.calls.every(call => !call.admin && call.method === 'GET'));
    assert.ok(!fixture.calls.some(call => call.endpoint.startsWith('git/ref/tags/')));
  } finally {
    globalThis.fetch = originalFetch;
    process.chdir(cwd);
    rmSync(root, { recursive: true, force: true });
  }
});

test('workflow wiring keeps authorization and every publisher consumer in one protected job', () => {
  const authority = readFileSync('.github/workflows/consolidated-release.yml', 'utf8');
  const docker = readFileSync('.github/workflows/docker-publish.yml', 'utf8');
  const publishJob = docker.split('\n  publish:')[1];
  assert.match(publishJob, /environment: .+release-stable.+release-insider/);
  assert.ok(publishJob.indexOf('actions/create-github-app-token@fee1f7d63c2ff003460e3d139729b119787bc349') <
    publishJob.indexOf('release-control.mjs authorize'));
  assert.ok(publishJob.indexOf('release-control.mjs authorize') <
    publishJob.indexOf('RELEASE_REGISTRY_TOKEN'));
  assert.ok(publishJob.indexOf('release-set.mjs tag') <
    publishJob.indexOf('release-control.mjs advance'));
  const preApproval = authority.split('\n  admit:')[1].split('\n  publish:')[0];
  assert.doesNotMatch(preApproval,
    /RELEASE_PUBLISHER_PRIVATE_KEY|RELEASE_REGISTRY_TOKEN|create-github-app-token|contents: write|packages: write|id-token: write/);
  for (const match of (authority + docker).matchAll(/\b([A-Z_]*RELEASE_IDENTITY)\s*[:=]/g)) {
    assert.equal(match[1], 'PRINTFARMER_RELEASE_IDENTITY', 'Only the public frontend identity may be transported');
  }
  assert.doesNotMatch(authority + docker, /outputs\.identity\b/);
  for (const match of (authority + docker).matchAll(/steps\.(?:authorize|consume)\.outputs\.(\w+)/g)) {
    assert.ok(['public_identity', 'verified_branch_head', 'frontend_identity', 'version', 'container_version', 'channel', 'identity_hash',
      'source_archive_url', 'sbom_url'].includes(match[1]), match[1]);
  }
  assert.match(docker, /run: node scripts\/ci\/release-control\.mjs consume/);
  assert.match(readFileSync('.dockerignore', 'utf8'), /\*\*\/\.artifacts\//);
  assert.match(readFileSync('.gitignore', 'utf8'), /^\.artifacts\/$/m);
});

test('source publication executes fail-closed inventory guards before persistent writes', () => {
  const docker = readFileSync('.github/workflows/docker-publish.yml', 'utf8').replace(/\r\n/g, '\n');
  const sourcePublication = docker.split('      - name: Publish and verify public corresponding-source assets\n')[1]
    .split('      - name: Promote validated immutable image tags')[0];
  assert.match(sourcePublication, /expected_source_assets=\(/);
  assert.match(sourcePublication, /expected_manifest_assets=\(\n            release-manifest\.json\n            release-manifest\.envelope\.json\n            release-manifest\.envelope\.bundle\.json/);
  assert.match(sourcePublication,
    /\[\[ "\$remote_inventory" == "\$expected_source_inventory" \|\|\n             "\$remote_inventory" == "\$expected_complete_inventory" \]\]/);

  const productionFunction = name => {
    const found = sourcePublication.match(new RegExp(`          ${name}\\(\\) \\{\\n([\\s\\S]*?)\\n          \\}`))?.[0];
    assert.ok(found, `Expected production ${name} guard`);
    return found.replace(/^          /gm, '');
  };
  const localGuard = productionFunction('validate_local_source_inventory');
  const existingGuard = productionFunction('validate_preexisting_source_inventory');
  const createdGuard = productionFunction('verify_new_release_is_empty');
  const shell = process.platform === 'win32' ? 'C:\\Program Files\\Git\\bin\\bash.exe' : 'bash';
  const source = ['source.tar.gz', 'source.json'];
  const manifest = ['release-manifest.json', 'release-manifest.envelope.json', 'release-manifest.envelope.bundle.json'];
  const exactInventory = assets => [...assets].sort().join('\n');
  const executeGuard = ({ local = [], remote = [], operation }) => {
    const root = resolve('.artifacts', `source-inventory-${process.pid}-${Math.random().toString(16).slice(2)}`);
    mkdirSync(resolve(root, 'release-assets'), { recursive: true });
    for (const asset of local) writeFileSync(resolve(root, 'release-assets', asset), 'fixture');
    const script = `${localGuard}
${existingGuard}
${createdGuard}
expected_source_inventory='source.json
source.tar.gz'
expected_complete_inventory='release-manifest.envelope.bundle.json
release-manifest.envelope.json
release-manifest.json
source.json
source.tar.gz'
gh() { printf '%s\n' "$REMOTE_INVENTORY"; }
${operation}`;
    const result = spawnSync(shell, ['-c', script], {
      cwd: root,
      encoding: 'utf8',
      env: { ...process.env, VERSION: 'v1.2.3', REMOTE_INVENTORY: exactInventory(remote) },
    });
    rmSync(root, { recursive: true, force: true });
    return result;
  };
  assert.equal(executeGuard({ operation: 'validate_local_source_inventory' }).status, 0);
  assert.equal(executeGuard({ local: source, operation: 'validate_local_source_inventory' }).status, 0);
  assert.notEqual(executeGuard({ local: ['unexpected.txt'], operation: 'validate_local_source_inventory' }).status, 0,
    'Unexpected local assets block before source construction');
  assert.equal(executeGuard({ remote: source, operation: 'validate_preexisting_source_inventory' }).status, 0);
  assert.equal(executeGuard({ remote: [...source, ...manifest], operation: 'validate_preexisting_source_inventory' }).status, 0,
    'Retry after manifest upload proceeds to manifest verification and pointer advancement');
  for (const remote of [[], source.slice(1), [...source, manifest[0]], [...source, 'unexpected.txt'], [...source, ...source]]) {
    assert.notEqual(executeGuard({ remote, operation: 'validate_preexisting_source_inventory' }).status, 0,
      `Invalid pre-existing inventory is rejected: ${remote.join(',')}`);
  }
  assert.equal(executeGuard({ operation: 'verify_new_release_is_empty' }).status, 0);
  assert.notEqual(executeGuard({ remote: ['concurrent.txt'], operation: 'verify_new_release_is_empty' }).status, 0,
    'A concurrent asset on a newly created release blocks before upload');
  const releaseExists = sourcePublication.indexOf('if gh release view "$VERSION" >/dev/null 2>&1; then');
  const guardInvocation = sourcePublication.indexOf('validate_preexisting_source_inventory', releaseExists);
  assert.ok(guardInvocation > releaseExists && guardInvocation < sourcePublication.indexOf('gh release upload'),
    'Existing inventory is validated before any source asset upload');
  assert.ok(docker.indexOf('Publish and verify signed manifest before mutable aliases') <
    docker.indexOf('Advance the complete channel pointer last'));
});

test('signed-artifact verification fails closed without logging payloads or accepting full JSON transport', async () => {
  const root = resolve('.artifacts', `signature-gate-${process.pid}`);
  const cwd = process.cwd();
  mkdirSync(root, { recursive: true });
  process.chdir(root);
  try {
    const identity = await authorizedRecord();
    writeAuthorization(identity);
    const env = { GITHUB_REPOSITORY: context().repository, GITHUB_REF: context().ref,
      RELEASE_SIGNER_IDENTITY: publisherWorkflowIdentity,
      RELEASE_PUBLIC_IDENTITY: JSON.stringify(publicAuthorization(identity)) };
    assert.throws(() => verifyAuthorization(env, () => { throw new Error(JSON.stringify(identity)); }),
      error => error.message === 'Authorization signature verification failed');
    const verify = (file, args) => {
      assert.equal(file, 'cosign');
      assert.ok(args.includes(authorizationPath) && args.includes(authorizationBundle));
      assert.ok(args.includes(publisherWorkflowIdentity));
      assert.doesNotMatch(JSON.stringify(args), /privateMarker|private-value/);
    };
    assert.deepEqual(verifyAuthorization(env, verify), identity);
    assert.throws(() => verifyAuthorization({ ...env, RELEASE_IDENTITY: JSON.stringify(identity) }, verify), /forbidden/);
    writeFileSync(authorizationPath, JSON.stringify({ ...identity, protection: {} }));
    assert.throws(() => verifyAuthorization(env, verify), /differs from public identity/);
    writeFileSync(authorizationPath, '{"privateMarker": private-value}');
    assert.throws(() => verifyAuthorization(env, verify),
      error => error.message === 'Private authorization unavailable or malformed');
    writeAuthorization(identity);
    assert.throws(() => verifyAuthorization({ ...env, RELEASE_PUBLIC_IDENTITY: JSON.stringify({
      ...publicAuthorization(identity), futurePrivate: 'private-value',
    }) }, verify), /Invalid public authorization/);
    assert.throws(() => verifyAuthorization({ ...env, GITHUB_REF: 'refs/heads/attacker' }, verify), /Untrusted/);
    assert.throws(() => verifyAuthorization({
      ...env,
      RELEASE_SIGNER_IDENTITY: `https://github.com/${identity.workflowIdentity}`,
    }, verify), /Untrusted/);
  } finally {
    process.chdir(cwd);
    rmSync(root, { recursive: true, force: true });
  }
});

test('public assets, tag annotations and ledger retain hashes but no private or unknown authorization fields', async () => {
  const ledger = state();
  const identity = record(ledger);
  for (const field of ['futurePrivate', 'reviewerId', 'publisherId']) {
    const poisoned = structuredClone(ledger);
    poisoned.reservations[identity.allocationKey].record[field] = { value: 'private-value' };
    assert.throws(() => publicLedger(poisoned), ReleasePolicyError);
  }
  const set = completeSet(identity);
  set.futurePrivate = { value: 'private-future-value' };
  set.images.api.platforms['linux/amd64'].labels.futurePrivate = 'private-label';
  assert.throws(() => releaseManifest(identity, set, undefined, releaseNotesHash,
    releaseMetadataFixture(identity.baseVersion)), /public set fields|public set labels/);
  delete set.futurePrivate;
  delete set.images.api.platforms['linux/amd64'].labels.futurePrivate;
  advance(ledger, identity, set, sha, '');
  const sanitized = publicLedger(ledger);
  const serialized = JSON.stringify(sanitized);
  assert.doesNotMatch(serialized, /protection|rulesets|environment|reviewer|publisher|futurePrivate|private-/);
  assert.equal(sanitized.reservations[identity.allocationKey].identitySha256, hash(identity));
  assert.equal(sanitized.reservations[identity.allocationKey].setHash, publicSetHash(set));
  assert.deepEqual(publicLedger(sanitized), sanitized, 'Public ledger serialization must be idempotent');
  const root = resolve('.artifacts', `public-assets-${process.pid}`);
  try {
    emitPublicReleaseAssets(identity, set, root, releaseNotesHash);
    for (const file of ['release-identity.json', 'release-set.json']) {
      const content = readFileSync(resolve(root, 'release-assets', file), 'utf8');
      assert.doesNotMatch(content, /rulesets|environment|reviewer|private-publisher|futurePrivate|private-/);
      assert.ok(content.includes(hash(identity)));
    }
    const published = JSON.parse(readFileSync(resolve(root, 'release-assets/release-identity.json'), 'utf8'));
    assert.deepEqual(published, publicAuthorization(identity));
    assert.equal(existsSync(resolve(root, 'release-assets/release-manifest.json')), false);
    assert.equal(existsSync(resolve(root, 'release-assets/release-manifest.envelope.json')), false);
    const manifest = releaseManifest(identity, set, undefined, releaseNotesHash,
      releaseMetadataFixture(identity.baseVersion), cryptoEvidenceFixture(set));
    for (const mutate of [
      value => { value.lifecycle.cadence = 'manual'; },
      value => { value.provenance.source.commit = newerSha; },
      value => { value.provenance.workflow.allocationKey = 'invalid'; },
      value => { value.evidence.services.api.index.signature.subject = newerSha; },
      value => { value.evidence.services.api.platforms['linux/amd64'].sbom.subject = `sha256:${'f'.repeat(64)}`; },
      value => { value.compatibility.managedEligible = false; },
      value => { value.migration.providers.postgresql = 'unknown'; },
      value => { value.compatibility.updater.fixedSteps.pop(); },
      value => { value.consumption.publisherApproval = 'host-update'; },
      value => { value.consumption.autoUpdate.releaseSet = 'other-set'; },
      value => { value.consumption.autoUpdate.hostPolicy = 'publisher-selected'; },
      value => { value.unrecognized = true; },
    ]) {
      const changed = structuredClone(manifest);
      mutate(changed);
      assert.throws(() => validateReleaseManifest(changed), ReleasePolicyError);
    }
    for (const completeSet of [undefined, null, [], 'forged']) {
      assert.throws(() => validateReleaseManifest({ ...manifest, completeSet }),
        /release manifest (fields|complete set)/);
    }
    for (const identity of [undefined, null, [], 'forged']) {
      assert.throws(() => validateReleaseManifest({ ...manifest, identity }),
        /release manifest (fields|identity)/);
    }
    let tag;
    const store = memoryStore(ledger);
    await ensureSourceTag(async (endpoint, method, body) => {
      if (endpoint.startsWith('git/ref/tags/')) {
        if (!tag) throw Object.assign(new Error('missing'), { status: 404 });
        return { object: { sha: newerSha, type: 'tag' } };
      }
      if (endpoint === 'git/tags' && method === 'POST') {
        assert.deepEqual(JSON.parse(body.message), published);
        return { sha: newerSha };
      }
      if (endpoint === `git/tags/${newerSha}`) return { object: { sha, type: 'commit' } };
      if (endpoint === 'git/refs') { tag = true; return {}; }
      throw new Error('Unexpected fixture endpoint');
    }, store, identity, transact);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('the shared public field projection rejects wrong types rather than coercing private objects', () => {
  for (const field of [...publicIdentityFields, 'identitySha256', 'buildTime']) {
    for (const value of [123, {}, [], undefined, '', 'line\nbreak']) {
      assert.throws(() => publicIdentity({ ...record(), [field]: value }), /Invalid public identity field/);
    }
  }
  for (const source of [undefined, [], 123, 'identity']) assert.throws(() => publicIdentity(source));
  for (const value of [{ private: 'value' }, 42]) {
    assert.throws(() => buildMetadata({ ...record(), releaseId: value }), ReleasePolicyError);
  }
});

test('executed signing commands preserve signed subjects and never let verification rewrite bundles', async () => {
  const docker = readFileSync('.github/workflows/docker-publish.yml', 'utf8').replace(/\r\n/g, '\n');
  const authorizationSigning = docker.split('      - name: Sign immutable authorization\n')[1]
    .split('      - uses: actions/upload-artifact')[0].split('        run: |\n')[1]
    .split('\n').map(line => line.replace(/^          /, '')).join('\n');
  const signing = docker.split('      - name: Sign externally-digested complete release manifest\n')[1]
    .split('      - name: Persist signed manifest triplet before public upload')[0].split('        run: |\n')[1]
    .split('\n').map(line => line.replace(/^          /, '')).join('\n');
  const publication = docker.split('\n').filter(line =>
    /^\s+cp \.\.\/\.artifacts\/release-authorization\/public-identity/.test(line))
    .map(line => line.trim());
  assert.deepEqual(publication, [
    'cp ../.artifacts/release-authorization/public-identity.json release-assets/release-identity.json',
    'cp ../.artifacts/release-authorization/public-identity.bundle.json release-assets/release-identity.bundle.json',
  ]);
  assert.match(docker, /Sign externally-digested complete release manifest/);
  assert.match(docker, /release-manifest\.envelope\.bundle\.json/);
  assert.ok(docker.indexOf('Sign externally-digested complete release manifest') >
    docker.indexOf('Verify every pushed digest signature and SPDX attestation'));
  assert.ok(docker.indexOf('Sign externally-digested complete release manifest') <
    docker.indexOf('Publish and verify public corresponding-source assets'));
  assert.ok(docker.indexOf('Publish and verify public corresponding-source assets') <
    docker.indexOf('Promote validated immutable image tags'));
  assert.ok(docker.indexOf('Promote validated immutable image tags') <
    docker.indexOf('Publish and verify signed manifest before mutable aliases'));
  assert.ok(docker.indexOf('Publish and verify signed manifest before mutable aliases') <
    docker.indexOf('Publish channel-isolated verified image aliases'));
  assert.ok(docker.indexOf('Publish channel-isolated verified image aliases') <
    docker.indexOf('Advance the complete channel pointer last'));
  assert.match(docker, /const apiTransportBody = releasedBody\.replace\(\/\\r\\n\/g, "\\n"\)/);
  assert.match(docker, /gh release view "\$VERSION" --json body > "\$recovered\/release-body\.json"/);
  assert.match(readFileSync('scripts/ci/release-set.mjs', 'utf8'), /Expected inspect, tag, or alias/);
  const canonicalNotes = docker.split('      - name: Generate canonical release notes before signing\n')[1]
    .split('      - name: Validate complete immutable set')[0];
  assert.match(canonicalNotes, /node \.\.\/scripts\/ci\/release-notes\.mjs/);
  const generator = readFileSync('scripts/ci/release-notes.mjs', 'utf8');
  for (const section of ['Features', 'Fixes', 'Breaking changes', 'Compatibility', 'Migration', 'Downtime', 'Backup', 'Recovery']) {
    assert.match(generator, new RegExp(section));
  }
  assert.match(generator, /require.*metadata|Release metadata/);
  const uploadedAuthorizationFiles = artifactUploads('.github/workflows/docker-publish.yml').flat()
    .filter(path => path.startsWith('.artifacts/release-authorization/'));
  const root = resolve('.artifacts', `sign-public-${process.pid}`);
  const cwd = process.cwd();
  mkdirSync(root, { recursive: true });
  symlinkSync(resolve(cwd, 'scripts'), resolve(root, 'scripts'), 'junction');
  copyFileSync(resolve(cwd, 'release-trust-policy.json'), resolve(root, 'release-trust-policy.json'));
  mkdirSync(resolve(root, 'release-metadata'));
  const fixtureMetadata = readFileSync(resolve(cwd, 'release-metadata', '0.2.3.json'), 'utf8')
    .replaceAll('0.2.3', '1.2.3');
  writeFileSync(resolve(root, 'release-metadata', '1.2.3.json'), fixtureMetadata);
  process.chdir(root);
  try {
    const identity = await authorizedRecord();
    writeAuthorization(identity);
    writeAuthorizationSet(identity, completeSet(identity));
    const metadataBytes = readFileSync('release-metadata/1.2.3.json', 'utf8');
    const metadata = JSON.parse(metadataBytes);
    const sourceArtifacts = Object.fromEntries(Object.values(metadata.schemas).map(schema => [
      schema.artifact, Buffer.from(readFileSync(schema.artifact, 'utf8').replaceAll(/\r\n/g, '\n')),
    ]));
    mkdirSync('.artifacts/release-authorization', { recursive: true });
    writeFileSync('.artifacts/release-authorization/source-release-metadata.json', metadataBytes);
    const set = completeSet(identity);
    emitPublicReleaseAssets(identity, set, '.', releaseNotesHash, metadataBytes, sourceArtifacts,
      cryptoEvidenceFixture(set));
    const mock = `cosign() {
      local operation="$1" bundle="" source="" previous=""
      shift
      for arg in "$@"; do
        if [[ "$previous" == "--bundle" ]]; then bundle="$arg"; fi
        previous="$arg"
        source="$arg"
      done
      case "$operation" in
        sign-blob)
          printf '%s\\n' "$source" >> "$COSIGN_WITNESS"
          printf 'signed:%s' "$source" > "$bundle"
          ;;
        verify-blob)
          [[ -s "$bundle" && -f "$source" ]]
          ;;
        *) return 64 ;;
      esac
    }
    gh() {
      [[ "$1 $2" == "release view" ]] || return 70
      [[ "$GH_RELEASE_SCENARIO" == "absent" ]] && return 1
      return 70
    }\n`;
    const shell = process.platform === 'win32' ? 'C:\\Program Files\\Git\\bin\\bash.exe' : 'bash';
    const witness = resolve(root, 'cosign-subjects.txt');
    const authorizationResult = spawnSync(shell, ['-c', `${mock}${authorizationSigning}`],
      { cwd: root, encoding: 'utf8', env: {
        ...process.env, COSIGN_WITNESS: witness,
      } });
    assert.ifError(authorizationResult.error);
    assert.equal(authorizationResult.status, 0, authorizationResult.stderr);
    const result = spawnSync(shell, ['-c', `${mock}${signing}`],
      { cwd: root, encoding: 'utf8', env: {
        ...process.env, VERSION: identity.canonicalVersion, RELEASE_SIGNER_IDENTITY: publisherWorkflowIdentity,
        RELEASE_PUBLIC_IDENTITY: JSON.stringify(readReleaseManifest().manifest.identity),
        COSIGN_WITNESS: witness, GH_TOKEN: '', GH_RELEASE_SCENARIO: 'absent', RUNNER_TEMP: resolve(root, 'runner-temp'),
      } });
    assert.ifError(result.error);
    assert.equal(result.status, 0, result.stderr);
    assert.doesNotMatch(result.stdout + result.stderr, /private-publisher|private-future/);
    assert.equal(readFileSync(authorizationPath, 'utf8'), JSON.stringify(identity));
    assert.equal(readFileSync(manifestEnvelopeBundle, 'utf8'),
      `signed:${manifestEnvelopePath}`);
    assert.deepEqual(readFileSync(witness, 'utf8').trim().split('\n'), [
      authorizationPath,
      '.artifacts/release-authorization/public-identity.json',
      manifestEnvelopePath,
    ]);
    assert.ok(uploadedAuthorizationFiles.includes(manifestEnvelopeBundle));
    assert.doesNotMatch(readFileSync(manifestEnvelopeBundle, 'utf8'), privateFields);
  } finally {
    process.chdir(cwd);
    rmSync(root, { recursive: true, force: true });
  }
});

test('candidate CLI validates real ancestry and fails expiry, bare branches and publication', () => {
  const root = resolve('.artifacts', `candidate-cli-${process.pid}`);
  mkdirSync(resolve(root, '.github'), { recursive: true });
  try {
    const git = args => execFileSync('git', args, { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
    git(['init', '--quiet']);
    git(['-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '--quiet', '--allow-empty', '-m', 'fixture']);
    const plan = { branch: 'release/v1.2.3', target: '1.2.3', sourceCommit: git(['rev-parse', 'HEAD']).trim(),
      owner: 'maintainer', qualification: 'reviewed',
      created: new Date().toISOString(), expires: new Date(Date.now() + 86400000).toISOString() };
    const run = (branch, overrides = {}) => {
      writeFileSync(resolve(root, '.github', 'release-candidate.json'), JSON.stringify({ ...plan, ...overrides }));
      return spawnSync(process.execPath, [resolve('scripts/ci/validate-release-candidate.mjs')], {
        cwd: root, encoding: 'utf8',
        env: { ...process.env, CANDIDATE_BRANCH: branch, RELEASE_CANDIDATE_MAX_DAYS: '7' },
      });
    };
    assert.equal(run(plan.branch).status, 0);
    assert.equal(run('main').status, 0);
    assert.equal(run('release').status, 1);
    assert.equal(run(plan.branch, { publish: true }).status, 1);
    assert.equal(run(plan.branch, { sourceCommit: sha }).status, 1);
    assert.equal(run(plan.branch, { expires: '2000-01-01T00:00:00Z' }).status, 1);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
