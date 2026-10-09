// The installers' optional offline Sigstore trusted root (#3099): explicit only, fail closed,
// and the default online verification path is unchanged. A fake cosign records its arguments
// and refuses, so each case stops right after (or before) signature verification.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { after, describe, test } from 'node:test';
import { fileURLToPath } from 'node:url';

const scripts = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const version = '9.9.9-insider.1';
const isWindows = process.platform === 'win32';
const pwsh = spawnSync('pwsh', ['-NoProfile', '-Command', '$PSVersionTable.PSVersion.Major'], { encoding: 'utf8' });
const hasPwsh = pwsh.status === 0;
const isRoot = typeof process.getuid === 'function' && process.getuid() === 0;

const roots = [];
after(() => roots.forEach((root) => rmSync(root, { recursive: true, force: true })));

function workspace(runtime) {
  const root = mkdtempSync(join(tmpdir(), 'pf-installer-trust-'));
  roots.push(root);
  const bin = join(root, 'bin');
  const assets = join(root, 'assets');
  mkdirSync(bin);
  mkdirSync(assets);
  const log = join(root, 'cosign-args.txt');
  if (isWindows) {
    writeFileSync(join(bin, 'cosign.cmd'), `@echo off\r\necho %*>"${log}"\r\nexit /b 1\r\n`);
  } else {
    writeFileSync(join(bin, 'cosign'), `#!/bin/sh\nprintf '%s\\n' "$*" > '${log}'\nexit 1\n`);
    chmodSync(join(bin, 'cosign'), 0o755);
  }
  const prefix = `printfarmer-host-update-cli-v${version}`;
  for (const name of [`${prefix}-${runtime}.tar.gz`, `${prefix}-SHA256SUMS`, `${prefix}-SHA256SUMS.sigstore.json`]) {
    writeFileSync(join(assets, name), 'fixture\n');
  }
  const trustedRoot = join(root, 'trusted_root.json');
  writeFileSync(trustedRoot, '{"mediaType":"application/vnd.dev.sigstore.trustedroot+json;version=0.1"}\n');
  return { root, bin, assets, log, trustedRoot, installRoot: join(root, 'install') };
}

const separator = isWindows ? ';' : ':';
const env = (bin) => ({ ...process.env, PATH: `${bin}${separator}${process.env.PATH}` });

const installers = [
  {
    name: 'bash',
    skip: isWindows && 'bash installer runs on Linux hosts only',
    runtime: 'linux-x64',
    run(ws, extra) {
      return spawnSync('bash', [join(scripts, 'install-host-update-cli.sh'), 'install', '--version', version,
        '--asset-dir', ws.assets, '--install-root', ws.installRoot, '--runtime', 'linux-x64', ...extra],
      { encoding: 'utf8', env: env(ws.bin) });
    },
    option: '--trusted-root',
  },
  {
    name: 'powershell',
    skip: !hasPwsh && 'pwsh is not available',
    runtime: isWindows ? 'win-x64' : 'linux-x64',
    run(ws, extra) {
      return spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-File', join(scripts, 'install-host-update-cli.ps1'),
        'install', '-Version', version, '-AssetDir', ws.assets, '-InstallRoot', ws.installRoot,
        '-Runtime', isWindows ? 'win-x64' : 'linux-x64', ...extra], { encoding: 'utf8', env: env(ws.bin) });
    },
    option: '-TrustedRoot',
  },
];

const cosignArgs = (ws) => (existsSync(ws.log) ? readFileSync(ws.log, 'utf8') : null);

for (const installer of installers) {
  describe(`${installer.name} installer trusted root`, { skip: installer.skip }, () => {
    test('default path verifies against the public-good root (no trusted root passed)', () => {
      const ws = workspace(installer.runtime);
      const result = installer.run(ws, []);
      assert.equal(result.status, 1, result.stderr);
      const args = cosignArgs(ws);
      assert.ok(args, 'cosign must be invoked');
      assert.match(args, /verify-blob/);
      assert.doesNotMatch(args, /--trusted-root/);
      assert.match(`${result.stdout}${result.stderr}`, /not signed by the development release workflow/);
    });

    test('an explicit trusted root is passed to cosign for offline verification', () => {
      const ws = workspace(installer.runtime);
      const result = installer.run(ws, [installer.option, ws.trustedRoot]);
      assert.equal(result.status, 1, result.stderr);
      const args = cosignArgs(ws);
      assert.ok(args, 'cosign must be invoked');
      assert.ok(args.includes(`--trusted-root ${ws.trustedRoot}`), args);
    });

    test('the trusted root is never discovered from the environment', () => {
      const ws = workspace(installer.runtime);
      process.env.PRINTFARMER_TRUSTED_ROOT = ws.trustedRoot;
      process.env.SIGSTORE_TRUSTED_ROOT = ws.trustedRoot;
      process.env.TUF_ROOT = ws.root;
      try {
        const again = installer.run(ws, []);
        assert.equal(again.status, 1, again.stderr);
        assert.doesNotMatch(cosignArgs(ws), /--trusted-root/);
      } finally {
        delete process.env.PRINTFARMER_TRUSTED_ROOT;
        delete process.env.SIGSTORE_TRUSTED_ROOT;
        delete process.env.TUF_ROOT;
      }
    });

    const rejections = [
      { name: 'a relative path', status: 2, pattern: /must be an absolute path/, value: () => 'trusted_root.json' },
      { name: 'a missing file', status: 1, pattern: /not a regular file/, value: (ws) => join(ws.root, 'missing.json') },
      { name: 'a directory', status: 1, pattern: /not a regular file/, value: (ws) => ws.assets },
      {
        name: 'an empty file',
        status: 1,
        pattern: /unreadable or empty/,
        value: (ws) => {
          const empty = join(ws.root, 'empty.json');
          writeFileSync(empty, '');
          return empty;
        },
      },
      {
        name: 'a symbolic link',
        status: 1,
        pattern: /not a regular file/,
        skip: isWindows && 'creating symlinks needs elevation on Windows',
        value: (ws) => {
          const link = join(ws.root, 'link.json');
          symlinkSync(ws.trustedRoot, link);
          return link;
        },
      },
      {
        name: 'an unreadable file',
        status: 1,
        pattern: /unreadable or empty/,
        skip: (isWindows || isRoot) && 'file modes cannot deny read to this user',
        value: (ws) => {
          chmodSync(ws.trustedRoot, 0o000);
          return ws.trustedRoot;
        },
      },
    ];

    for (const rejection of rejections) {
      test(`${rejection.name} is refused before any verification`, { skip: rejection.skip }, () => {
        const ws = workspace(installer.runtime);
        const result = installer.run(ws, [installer.option, rejection.value(ws)]);
        assert.equal(result.status, rejection.status, `${result.stdout}${result.stderr}`);
        assert.match(`${result.stdout}${result.stderr}`, rejection.pattern);
        assert.equal(cosignArgs(ws), null, 'cosign must not run');
        assert.equal(existsSync(ws.installRoot), false, 'nothing is placed');
      });
    }

    test('the trusted root option may be given only once', () => {
      const ws = workspace(installer.runtime);
      const result = installer.run(ws, [installer.option, ws.trustedRoot, installer.option, ws.trustedRoot]);
      assert.equal(result.status, 2, `${result.stdout}${result.stderr}`);
      assert.match(`${result.stdout}${result.stderr}`, /only once/);
      assert.equal(cosignArgs(ws), null, 'cosign must not run');
    });
  });
}
