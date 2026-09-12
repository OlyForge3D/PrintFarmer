// Regression guard for Defect: docker-publish tag triggers must cover the
// exact release-tag set that consolidated-release.yml accepts. The release
// workflow polls docker-publish.yml for the exact tag; if docker-publish
// doesn't trigger for an accepted insider-channel tag, the release step times out.
//
// This test asserts:
//   1. The GH Actions tag globs accept stable and supported insider-channel tags.
//   2. The globs reject arbitrary suffixes, matching the release validators.
//   3. Stable and insider branch guards remain explicit.
//   4. Promotion keeps stable/latest and insider channel pointers isolated.

import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const repositoryRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  '..', '..', '..',
);

// Convert a GitHub Actions tag-filter glob to a JavaScript RegExp.
// Supports the subset used by docker-publish.yml:
//   [chars] — character class (passthrough)
//   +       — one-or-more of the preceding character or class
//   *       — zero-or-more of any character except '/'
//   ?       — zero-or-one of the preceding character or class
//   any other character is a literal (including '.' and '-')
function globToRegex(glob) {
  let regex = '^';
  let index = 0;
  while (index < glob.length) {
    const ch = glob[index];
    if (ch === '[') {
      const end = glob.indexOf(']', index);
      if (end === -1) {
        throw new Error(`Unterminated character class in glob: ${glob}`);
      }
      regex += glob.slice(index, end + 1);
      index = end + 1;
    } else if (ch === '+' || ch === '?') {
      regex += ch;
      index += 1;
    } else if (ch === '*') {
      regex += '[^/]*';
      index += 1;
    } else if ('.^$()|{}\\'.includes(ch)) {
      regex += `\\${ch}`;
      index += 1;
    } else {
      regex += ch;
      index += 1;
    }
  }
  regex += '$';
  return new RegExp(regex);
}

// Extract the `on.push.tags` block from docker-publish.yml as a list of
// raw glob strings. Parsed with a targeted line scanner (not a full YAML
// parser) so this test has no runtime deps beyond node:*.
async function loadDockerPublishTagGlobs() {
  const workflowPath = path.join(
    repositoryRoot, '.github', 'workflows', 'docker-publish.yml',
  );
  const source = await readFile(workflowPath, 'utf8');
  const lines = source.split(/\r?\n/);

  const pushIndex = lines.findIndex((line) => /^\s{2}push:\s*$/.test(line));
  assert.notEqual(pushIndex, -1, 'docker-publish.yml: `on.push` block not found');

  let tagsIndex = -1;
  for (let index = pushIndex + 1; index < lines.length; index += 1) {
    if (/^\s{4}tags:\s*$/.test(lines[index])) {
      tagsIndex = index;
      break;
    }
    // Stop at the next top-level `on:` sibling (workflow_dispatch, etc.)
    if (/^\s{2}\S/.test(lines[index])) {
      break;
    }
  }
  assert.notEqual(tagsIndex, -1, 'docker-publish.yml: `on.push.tags` block not found');

  const globs = [];
  for (let index = tagsIndex + 1; index < lines.length; index += 1) {
    const match = lines[index].match(/^\s{6}-\s+['"]([^'"]+)['"]\s*$/);
    if (match) {
      globs.push(match[1]);
      continue;
    }
    // Any other non-blank content at ≤6 spaces indent ends the list.
    if (lines[index].trim() !== '' && !/^\s{7,}/.test(lines[index])) {
      break;
    }
  }
  assert.ok(globs.length > 0, 'docker-publish.yml: no tag globs parsed');
  return globs;
}

async function loadReleasePolicy() {
  const workflowPath = path.join(
    repositoryRoot, '.github', 'workflows', 'consolidated-release.yml',
  );
  const source = await readFile(workflowPath, 'utf8');
  const matches = [...source.matchAll(/if\s+\[\[\s+!\s+"\$VERSION"\s+=~\s+(\^v[^\s]+)\s+\]\]/g)];
  assert.equal(matches.length, 2,
    'consolidated-release.yml: expected stable and insider version validators');
  return {
    source,
    stable: new RegExp(matches[0][1]),
    insider: new RegExp(matches[1][1]),
  };
}

test('docker-publish tag globs cover stable and insider release validators', async () => {
  const globs = await loadDockerPublishTagGlobs();
  const globRegexes = globs.map(globToRegex);
  const policy = await loadReleasePolicy();
  const matchesAnyGlob = (tag) => globRegexes.some((rx) => rx.test(tag));

  const accepted = [
    ['stable', 'v1.2.3'],
    ['stable', 'v0.0.0'],
    ['stable', 'v10.20.30'],
    ['insider', 'v1.2.3-insider.1'],
    ['insider', 'v1.2.3-insider.42'],
    ['insider', 'v1.2.3-beta.1'],
    ['insider', 'v1.2.3-rc.99'],
  ];
  for (const [channel, tag] of accepted) {
    assert.ok(policy[channel].test(tag),
      `${channel} release validator should accept ${tag}`);
    assert.ok(matchesAnyGlob(tag),
      `docker-publish.yml tag globs must match accepted release tag ${tag}`);
  }

  const rejected = [
    'v1.2.3-alpha.1',
    'v1.2.3-preview',
    'v1.2.3-insider',
    'v1.2.3-beta',
    'v1.2.3-rc',
    'v1.2.3-insider.1.2',
    'v1.2',
    'v1.2.3.4',
    'release-1.2.3',
    'v1.2.3-INSIDER.1',
  ];
  for (const tag of rejected) {
    assert.ok(!policy.stable.test(tag) && !policy.insider.test(tag),
      `release validators should reject ${tag}`);
    assert.ok(!matchesAnyGlob(tag),
      `docker-publish.yml tag globs must NOT match rejected tag ${tag}`);
  }

  assert.match(policy.source, /SOURCE_REF" != "refs\/heads\/main"/,
    'stable releases must remain bound to main');
  assert.match(policy.source, /SOURCE_REF" != "refs\/heads\/development"/,
    'insider releases must remain bound to development');
});

test('Docker promotion isolates stable and insider channel pointers', async () => {
  const workflowPath = path.join(
    repositoryRoot, '.github', 'workflows', 'docker-publish.yml',
  );
  const source = await readFile(workflowPath, 'utf8');

  assert.match(source, /TAGS\+=\("\$\{major\}\.\$\{minor\}" "\$major" stable latest\)/,
    'stable releases must promote stable, latest, major, and minor pointers');
  assert.match(source, /VERSION" =~ -\(insider\|beta\|rc\)\\\.\[0-9\]\+\$/,
    'supported prereleases must be recognized as insider-channel builds');
  assert.match(source, /TAGS\+=\(insider\)/,
    'insider releases must promote the insider pointer');
  assert.equal(
    (source.match(/org\.printfarmer\.release-channel=\$\{\{ steps\.source\.outputs\.channel \}\}/g) ?? []).length,
    2,
    'both split-service and monolith images must carry the release-channel label',
  );
});

test('legacy stable release path is main-only and cannot mark stable tags prerelease', async () => {
  const workflowPath = path.join(
    repositoryRoot, '.github', 'workflows', 'release.yml',
  );
  const source = await readFile(workflowPath, 'utf8');

  assert.match(source, /SOURCE_REF" != "refs\/heads\/main"/,
    'legacy stable releases must remain bound to main');
  assert.match(source, /prerelease: false/,
    'legacy stable releases must always publish as stable');
  assert.doesNotMatch(source, /^\s+prerelease:\s*\n\s+description:/m,
    'legacy stable workflow must not expose a prerelease toggle');
});
