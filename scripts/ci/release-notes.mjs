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

function validatedOperationalNotes(notes) {
  const noteFields = ['compatibility', 'migration', 'downtime', 'backup', 'recovery'];
  requireThat(notes && typeof notes === 'object' && !Array.isArray(notes) &&
    Object.keys(notes).length === noteFields.length &&
    noteFields.every(field => Object.hasOwn(notes, field)),
  'Release metadata notes have unknown or missing fields');
  for (const field of noteFields) requireText(notes[field], field);
  return notes;
}

function escapeMarkdownText(value) {
  return value.replace(/\\/g, '\\\\').replace(/[`*_~[\]<>&]/g, '\\$&');
}

export function validateReleaseNotesMetadata(metadata, version) {
  requireThat(metadata && typeof metadata === 'object' && !Array.isArray(metadata),
    'Release metadata is malformed');
  if (metadata.schema === 2) {
    requireThat(metadata.version === version && metadata.notes && typeof metadata.notes === 'object',
      'Release metadata version does not match the release');
    return validateReleaseNotesMetadata({ schema: 1, version, ...metadata.notes }, version);
  }
  if (metadata.schema === 3) {
      const fields = ['schema', 'version', 'releasePaths', 'minimumUpdater', 'components',
        'schemas', 'migrations', 'operations', 'rollback', 'notes'];
      requireThat(Object.keys(metadata).length === fields.length && fields.every(field => Object.hasOwn(metadata, field)) &&
        metadata.version === version && metadata.notes && typeof metadata.notes === 'object' && !Array.isArray(metadata.notes),
      'Release metadata version does not match the release');
      return validatedOperationalNotes(metadata.notes);
  }
  const fields = ['schema', 'version', 'compatibility', 'migration', 'downtime', 'backup', 'recovery'];
  requireThat(Object.keys(metadata).length === fields.length && fields.every(field => Object.hasOwn(metadata, field)),
    'Release metadata has unknown or missing fields');
  requireThat(metadata.schema === 1 && metadata.version === version,
    'Release metadata version does not match the release');
  for (const field of fields.slice(2)) requireText(metadata[field], field);
  return metadata;
}

export function releaseNotes({ version, sourceCommit, previousTag, pullRequests, changelog, metadata, repository = 'OlyForge3D/PrintFarmer' }) {
  const baseVersion = version.replace(/-(?:insider|beta|rc)\.\d+$/, '');
  requireThat(typeof repository === 'string' && /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository),
    'Release notes require a canonical repository name');
  requireThat(/^[a-f0-9]{40}$/.test(sourceCommit), 'Release notes require the exact source commit');
  requireThat(typeof previousTag === 'string' && /^v\d+\.\d+\.\d+/.test(previousTag),
    'Release notes require a previous canonical version tag');
  requireThat(Array.isArray(pullRequests) && pullRequests.length > 0,
    'Release notes require at least one merged pull request in the release range');
  requireThat(typeof changelog === 'string' && changelog.trim().length > 0,
    'Release notes require a matching changelog entry');
  const entries = pullRequests.map(pr => {
    requireThat(Number.isSafeInteger(pr.number) && pr.number > 0 && typeof pr.title === 'string' &&
      pr.title.trim().length > 0 && !/[\r\n]/.test(pr.title) && typeof pr.url === 'string' &&
      pr.url === `https://github.com/${repository}/pull/${pr.number}` &&
      !/[\r\n]/.test(pr.url),
    'Release notes contain malformed merged pull request data');
    return `- [#${pr.number}](${pr.url}): ${escapeMarkdownText(pr.title.trim())}`;
  });
  const operational = Object.hasOwn(metadata ?? {}, 'schema')
    ? validateReleaseNotesMetadata(metadata, baseVersion)
    : validatedOperationalNotes(metadata);
  return `## PrintFarmer ${version}\n\nSource commit: ${sourceCommit}\nRelease range: ${previousTag}...${sourceCommit}\n\n` +
    `### Merged pull requests\n\n${entries.join('\n')}\n\n${changelog.trim()}\n\n### Compatibility\n\n${operational.compatibility}\n\n` +
    `### Migration\n\n${operational.migration}\n\n### Downtime\n\n${operational.downtime}\n\n### Backup\n\n${operational.backup}\n\n### Recovery\n\n${operational.recovery}\n`;
}

export function changelogEntry(changelog, version) {
  const match = changelog.match(new RegExp(`^## \\[?${version.replace(/[.]/g, '\\.')}\\]?[^\\n]*\\n([\\s\\S]*?)(?=^## |(?![\\s\\S]))`, 'm'));
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
  const rawVersion = process.env.VERSION;
  const sourceCommit = process.env.SOURCE_COMMIT;
  const sourceTag = process.env.SOURCE_TAG;
  const repository = process.env.GITHUB_REPOSITORY;
  const output = process.env.RELEASE_NOTES_OUTPUT;
  requireText(rawVersion, 'VERSION'); requireText(sourceTag, 'SOURCE_TAG'); requireText(repository, 'GITHUB_REPOSITORY'); requireText(output, 'RELEASE_NOTES_OUTPUT');
  requireThat(/^v?\d+\.\d+\.\d+(?:-(?:insider|beta|rc)\.\d+)?$/.test(rawVersion), 'Release notes require a canonical version');
  const version = rawVersion.startsWith('v') ? rawVersion.slice(1) : rawVersion;
  requireThat(sourceTag === `v${version}`, 'Release notes source tag does not match canonical version');
  requireThat(/^[a-f0-9]{40}$/.test(sourceCommit || ''), 'Release notes require the exact source commit');
  const tags = command('git', ['for-each-ref', '--merged', sourceCommit, '--sort=-v:refname',
    '--format=%(refname:strip=2)', 'refs/tags']).trim().split(/\r?\n/);
  const previousTag = tags.find(tag => tag !== sourceTag && /^v\d+\.\d+\.\d+/.test(tag));
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
    changelog: changelogEntry(readFileSync('CHANGELOG.md', 'utf8'), baseVersion),
    metadata: validateReleaseNotesMetadata(metadata, baseVersion), repository });
  writeFileSync(output, notes);
}

if (process.argv[1]?.endsWith('release-notes.mjs')) {
  try { main(); } catch (error) { console.error(error.message); process.exitCode = 1; }
}
