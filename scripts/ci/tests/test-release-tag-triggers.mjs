// Regression guard for Defect: docker-publish tag triggers must cover the
// exact release-tag set that consolidated-release.yml accepts. The release
// workflow polls docker-publish.yml for the exact tag; if docker-publish
// doesn't trigger for a `-beta.N` / `-rc.N` tag, the release step times out.
//
// This test asserts:
//   1. The GH Actions tag globs in docker-publish.yml accept `v1.2.3`,
//      `v1.2.3-beta.1`, and `v1.2.3-rc.1`.
//   2. The tag globs REJECT arbitrary suffixes such as `v1.2.3-alpha.1`
//      and `v1.2.3-anything`, matching the release validator's accepted set.
//   3. The globs and the release validator regex agree on the entire
//      sample set — no drift between the two.

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

// Extract the validation regex from consolidated-release.yml. It lives in
// the "Parse version" step as a Bash `=~` operand: ^v[0-9]+\.[0-9]+\.[0-9]+(-beta\.[0-9]+|-rc\.[0-9]+)?$
async function loadReleaseValidatorRegex() {
  const workflowPath = path.join(
    repositoryRoot, '.github', 'workflows', 'consolidated-release.yml',
  );
  const source = await readFile(workflowPath, 'utf8');
  const match = source.match(/if\s+\[\[\s+!\s+"\$VERSION"\s+=~\s+(\^v[^\s]+)\s+\]\]/);
  assert.ok(match, 'consolidated-release.yml: version validator regex not found');
  // Translate Bash regex escapes (\.) to JS regex form — same syntax here.
  return new RegExp(match[1]);
}

test('docker-publish tag globs cover the release validator\'s accepted set', async () => {
  const globs = await loadDockerPublishTagGlobs();
  const globRegexes = globs.map(globToRegex);
  const validator = await loadReleaseValidatorRegex();

  const matchesAnyGlob = (tag) => globRegexes.some((rx) => rx.test(tag));

  // Positive vectors — tags the release workflow can produce.
  const accepted = [
    'v1.2.3',
    'v0.0.0',
    'v10.20.30',
    'v1.2.3-beta.1',
    'v1.2.3-beta.42',
    'v1.2.3-rc.1',
    'v1.2.3-rc.99',
  ];
  for (const tag of accepted) {
    assert.ok(validator.test(tag),
      `release validator should accept ${tag} (test-vector self-check)`);
    assert.ok(matchesAnyGlob(tag),
      `docker-publish.yml tag globs must match accepted release tag ${tag}`);
  }

  // Negative vectors — the globs and the validator must both reject these.
  // This proves the globs did not over-broaden (e.g. via a bare `v*` pattern)
  // and stay in sync with consolidated-release.yml's accepted set.
  const rejected = [
    'v1.2.3-alpha.1',
    'v1.2.3-preview',
    'v1.2.3-beta',        // missing .N
    'v1.2.3-rc',          // missing .N
    'v1.2.3-beta.1.2',    // extra segment
    'v1.2',               // truncated
    'v1.2.3.4',           // extra segment
    'release-1.2.3',      // wrong prefix
    'v1.2.3-BETA.1',      // wrong case
  ];
  for (const tag of rejected) {
    assert.ok(!validator.test(tag),
      `release validator should reject ${tag} (test-vector self-check)`);
    assert.ok(!matchesAnyGlob(tag),
      `docker-publish.yml tag globs must NOT match rejected tag ${tag}`);
  }
});
