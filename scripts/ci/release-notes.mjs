import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';

function requireThat(condition, message) {
  if (!condition) throw new Error(message);
}

function requireText(value, field) {
  requireThat(typeof value === 'string' && value.trim().length > 0 && !/[\r\n]/.test(value),
    `Release metadata requires ${field}`);
}

export function validateReleaseNotesMetadata(metadata, version) {
  requireThat(metadata && typeof metadata === 'object' && !Array.isArray(metadata),
    'Release metadata is malformed');
  const fields = ['schema', 'version', 'compatibility', 'migration', 'downtime', 'backup', 'recovery'];
  requireThat(Object.keys(metadata).length === fields.length && fields.every(field => Object.hasOwn(metadata, field)),
    'Release metadata has unknown or missing fields');
  requireThat(metadata.schema === 1 && metadata.version === version,
    'Release metadata version does not match the release');
  for (const field of fields.slice(2)) requireText(metadata[field], field);
  return metadata;
}

export function releaseNotes({ version, sourceCommit, previousTag, pullRequests, changelog, metadata }) {
  validateReleaseNotesMetadata(metadata, version.replace(/-(?:insider|beta|rc)\.\d+$/, ''));
  requireThat(/^[a-f0-9]{40}$/.test(sourceCommit), 'Release notes require the exact source commit');
  requireThat(typeof previousTag === 'string' && /^v\d+\.\d+\.\d+/.test(previousTag),
    'Release notes require a previous canonical release tag');
  requireThat(Array.isArray(pullRequests) && pullRequests.length > 0,
    'Release notes require at least one merged pull request in the release range');
  requireThat(typeof changelog === 'string' && changelog.trim().length > 0,
    'Release notes require a matching changelog entry');
  const entries = pullRequests.map(pr => {
    requireThat(Number.isSafeInteger(pr.number) && pr.number > 0 && typeof pr.title === 'string' &&
      pr.title.trim().length > 0 && typeof pr.url === 'string' && /^https:\/\/github\.com\//.test(pr.url),
    'Release notes contain malformed merged pull request data');
    return `- [#${pr.number}](${pr.url}): ${pr.title.trim()}`;
  });
  return `## PrintFarmer ${version}\n\nSource commit: ${sourceCommit}\nRelease range: ${previousTag}...${sourceCommit}\n\n` +
    `### Merged pull requests\n\n${entries.join('\n')}\n\n${changelog.trim()}\n\n### Compatibility\n\n${metadata.compatibility}\n\n` +
    `### Migration\n\n${metadata.migration}\n\n### Downtime\n\n${metadata.downtime}\n\n### Backup\n\n${metadata.backup}\n\n### Recovery\n\n${metadata.recovery}\n`;
}

function changelogEntry(changelog, version) {
  const match = changelog.match(new RegExp(`^## \\[?${version.replace(/[.]/g, '\\.')}\\]?[^\\n]*\\n([\\s\\S]*?)(?=^## |$)`, 'm'));
  requireThat(match, `Release notes require a ${version} CHANGELOG entry`);
  const entry = match[1].trim();
  for (const heading of ['Features', 'Fixes', 'Breaking changes']) {
    const section = entry.match(new RegExp(`^### ${heading}\\s*$\\n([\\s\\S]*?)(?=^### |$)`, 'm'));
    requireThat(section && (section[1].trim() === 'None.' || section[1].trim() === 'N/A' || section[1].trim().length > 0),
      `Release notes CHANGELOG entry requires ${heading}`);
  }
  return entry;
}

function command(name, args) {
  return execFileSync(name, args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
}

function main() {
  const version = process.env.VERSION;
  const sourceCommit = process.env.SOURCE_COMMIT;
  const repository = process.env.GITHUB_REPOSITORY;
  const output = process.env.RELEASE_NOTES_OUTPUT;
  requireText(version, 'VERSION'); requireText(repository, 'GITHUB_REPOSITORY'); requireText(output, 'RELEASE_NOTES_OUTPUT');
  requireThat(/^[a-f0-9]{40}$/.test(sourceCommit || ''), 'Release notes require the exact source commit');
  const tags = command('git', ['for-each-ref', '--merged', sourceCommit, '--sort=-v:refname',
    '--format=%(refname:strip=2)', 'refs/tags']).trim().split(/\r?\n/);
  const previousTag = tags.find(tag => tag !== `v${version}` && /^v\d+\.\d+\.\d+/.test(tag));
  requireThat(previousTag, 'Release notes require a previous canonical release tag');
  const commitShas = command('gh', ['api', `repos/${repository}/compare/${previousTag}...${sourceCommit}`, '--jq', '.commits[].sha'])
    .trim().split(/\r?\n/).filter(Boolean);
  const pullRequests = new Map();
  for (const commit of commitShas) {
    const rows = JSON.parse(command('gh', ['api', `repos/${repository}/commits/${commit}/pulls`]));
    for (const pr of rows.filter(item => item.merged_at)) pullRequests.set(pr.number, { number: pr.number, title: pr.title, url: pr.html_url });
  }
  const baseVersion = version.replace(/-(?:insider|beta|rc)\.\d+$/, '');
  const metadata = JSON.parse(readFileSync(resolve('release-metadata', `${baseVersion}.json`), 'utf8'));
  const notes = releaseNotes({ version, sourceCommit, previousTag, pullRequests: [...pullRequests.values()].sort((a, b) => a.number - b.number),
    changelog: changelogEntry(readFileSync('CHANGELOG.md', 'utf8'), baseVersion), metadata: validateReleaseNotesMetadata(metadata, baseVersion) });
  writeFileSync(output, notes);
}

if (process.argv[1]?.endsWith('release-notes.mjs')) {
  try { main(); } catch (error) { console.error(error.message); process.exitCode = 1; }
}
