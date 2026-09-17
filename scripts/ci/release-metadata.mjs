import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { parseTag, requireThat } from './release-policy.mjs';

export function buildMetadata({ version, channel, sourceCommit, buildId }) {
  const parsed = parseTag(`v${version}`);
  requireThat(parsed.channel === channel && /^[a-f0-9]{40}$/.test(sourceCommit) &&
    /^[1-9][0-9]*$/.test(buildId), 'Invalid release build metadata');
  requireThat(parsed.baseVersion.split('.').every(part => BigInt(part) <= 65534n), 'Version exceeds assembly limits');
  return {
    props: `<Project>
  <PropertyGroup>
    <Version>${version}</Version>
    <AssemblyVersion>${parsed.baseVersion}.0</AssemblyVersion>
    <FileVersion>${parsed.baseVersion}.0</FileVersion>
    <InformationalVersion>${version}+sha.${sourceCommit}</InformationalVersion>
    <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
  </PropertyGroup>
</Project>
`,
    frontend: JSON.stringify({ service: 'frontend', commit: sourceCommit, sourceCommit,
      canonicalVersion: version, baseVersion: parsed.baseVersion, channel, sourceTag: `v${version}`, buildId }),
  };
}

export function emitBuildMetadata(record, root) {
  const metadata = buildMetadata(record);
  mkdirSync(join(root, 'src', 'Web', 'ReactApp', 'public'), { recursive: true });
  writeFileSync(join(root, 'src', 'ReleaseIdentity.props'), metadata.props);
  writeFileSync(join(root, 'src', 'Web', 'ReactApp', 'public', 'release-identity.json'), metadata.frontend);
}
