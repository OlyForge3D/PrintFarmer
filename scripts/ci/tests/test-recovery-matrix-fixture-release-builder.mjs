import assert from 'node:assert/strict';
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';

import { components } from '../release-policy.mjs';
import {
  completeHostUpdateCliSums,
  fixtureRelease,
  hostUpdateCliToolIdentity,
  protectedBackupReference,
  writeTrustedRootApproval,
} from '../recovery-matrix/fixture-release-builder.mjs';
import {
  hostUpdateCliArchiveName,
  hostUpdateCliRuntimes,
  hostUpdateCliSbomName,
  hostUpdateCliSumsName,
} from '../host-update-cli-package.mjs';

const scratchRoot = path.resolve('.recovery-matrix-test-work');

test('fixtureRelease emits release identities accepted by release-manifest sequencing', () => {
  const release = fixtureRelease({
    version: '1.2.3-insider.4',
    sourceCommit: 'a'.repeat(40),
    buildId: '3099',
  });
  assert.deepEqual(release, {
    version: '1.2.3-insider.4',
    tag: 'v1.2.3-insider.4',
    channel: 'insider',
    sourceBranch: 'development',
    sourceCommit: 'a'.repeat(40),
    buildId: '3099',
    sequence: 10020000300004,
  });
});

test('protectedBackupReference is closed, host-local, and prior-version bound', () => {
  const prior = fixtureRelease({ version: '1.0.0-insider.1', sourceCommit: 'b'.repeat(40) });
  const reference = protectedBackupReference(prior, { sha256: 'c'.repeat(64) });
  assert.deepEqual(Object.keys(reference).sort(), ['id', 'locationClass', 'releaseVersion', 'sha256']);
  assert.equal(reference.locationClass, 'host-local');
  assert.equal(reference.releaseVersion, prior.version);
  assert.equal(reference.sha256, 'c'.repeat(64));
});

test('trusted-root approval binds exact trusted-root bytes', () => {
  const scratch = path.join(scratchRoot, `root-approval-${process.pid}-${Date.now()}`);
  mkdirSync(scratch, { recursive: true });
  try {
    const approvalPath = path.join(scratch, 'approval.json');
    const bytes = Buffer.from('{"trusted":true}\n');
    const approval = writeTrustedRootApproval(approvalPath, bytes, {
      approvedAt: '2026-09-26T18:00:00.000Z',
      approvedBy: 'ci',
    });
    const stored = JSON.parse(readFileSync(approvalPath, 'utf8'));
    assert.equal(stored.trustedRootSha256, approval.trustedRootSha256);
    assert.equal(stored.kind, 'printfarmer-trusted-root-approval');
    assert.equal(stored.approvedBy, 'ci');
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});

test('completeHostUpdateCliSums names every supported runtime while allowing one carried runtime', () => {
  const scratch = path.join(scratchRoot, `cli-sums-${process.pid}-${Date.now()}`);
  mkdirSync(scratch, { recursive: true });
  try {
    const version = '1.0.0-insider.1';
    const carried = [
      hostUpdateCliArchiveName(version, 'linux-x64'),
      hostUpdateCliSbomName(version, 'linux-x64'),
    ];
    for (const name of carried) writeFileSync(path.join(scratch, name), `${name}\n`);
    const entries = completeHostUpdateCliSums({ assets: scratch, version });
    assert.equal(entries.length, hostUpdateCliRuntimes.length * 2);
    const names = entries.map((entry) => entry.name);
    for (const rid of hostUpdateCliRuntimes) {
      assert.ok(names.includes(hostUpdateCliArchiveName(version, rid)));
      assert.ok(names.includes(hostUpdateCliSbomName(version, rid)));
    }
    const text = readFileSync(path.join(scratch, hostUpdateCliSumsName(version)), 'utf8');
    assert.match(text, /^[a-f0-9]{64}  printfarmer-host-update-cli-v1\.0\.0-insider\.1-linux-x64\.tar\.gz/m);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});

test('hostUpdateCliToolIdentity records version, runtime, and installed archive sha256', () => {
  const scratch = path.join(scratchRoot, `cli-tool-identity-${process.pid}-${Date.now()}`);
  mkdirSync(scratch, { recursive: true });
  try {
    const version = '1.0.0-insider.1';
    const archiveName = hostUpdateCliArchiveName(version, 'linux-x64');
    writeFileSync(path.join(scratch, archiveName), 'fixture archive\n');
    assert.match(
      hostUpdateCliToolIdentity({ assets: scratch, version }),
      /^1\.0\.0-insider\.1\/linux-x64 sha256:[a-f0-9]{64}$/,
    );
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});

test('release policy still exposes all fixture image components expected by C2', () => {
  assert.deepEqual(Object.keys(components).sort(), [
    'api',
    'frontend',
    'monolith',
    'orcaslicer-worker',
    'printer-discovery',
    'slicer-host',
  ]);
});
