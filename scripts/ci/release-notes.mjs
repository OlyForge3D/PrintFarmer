import { components, repository } from './release-policy.mjs';
import { imageRepository } from './release-set.mjs';

export async function releaseNotes(api, release, digests) {
  const generated = await api('releases/generate-notes', { method: 'POST',
    body: { tag_name: release.tag, target_commitish: release.sourceCommit } });
  if (typeof generated?.body !== 'string') throw new Error('GitHub release notes are unavailable');
  const images = Object.entries(components).map(([name, { platforms }]) =>
    `| ${name} | ${platforms.join(', ')} | \`${imageRepository(name)}@${digests[name]}\` |`);
  return `## PrintFarmer ${release.version}\n\n` +
    `Channel: **${release.channel}**. Source: \`${release.sourceCommit}\`.\n\n` +
    `Build: https://github.com/${repository}/actions/runs/${release.buildId}\n\n` +
    `### Installation\n\nManual installation only. This publication is not a signed managed-update candidate ` +
    `and does not authorize installation or Auto-update. Back up your data, stop active prints, and follow ` +
    `[the deployment guide](https://github.com/${repository}/blob/${release.sourceCommit}/docs/DEPLOYMENT.md) ` +
    `before changing containers. Review migrations and recovery requirements for your installation.\n\n` +
    `### Images\n\n| Image | Platforms | Pinned reference |\n| --- | --- | --- |\n${images.join('\n')}\n\n` +
    `### Changes\n\n${generated.body}\n`;
}
