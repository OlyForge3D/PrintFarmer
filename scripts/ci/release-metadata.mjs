import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { hash, identityLabels, parseTag, requireThat } from './release-policy.mjs';
import { publicIdentityFields } from '../../src/Web/ReactApp/public-release-identity.mjs';
import { publicAuthorization } from './release-authorization.mjs';

export function buildMetadata(record) {
  const parsed = parseTag(record.sourceTag);
  requireThat(parsed.canonicalVersion === record.canonicalVersion &&
    parsed.baseVersion === record.baseVersion && parsed.channel === record.channel &&
    /^[a-f0-9]{40}$/.test(record.sourceCommit), 'Invalid build identity');
  for (const component of record.baseVersion.split('.')) {
    requireThat(BigInt(component) <= 65534n, 'Base exceeds .NET assembly version projection limits');
  }
  const escape = value => String(value).replaceAll('&', '&amp;').replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&apos;');
  const projection = publicAuthorization(record);
  const fields = publicIdentityFields;
  requireThat(fields.every(field => typeof projection[field] === 'string'), 'Incomplete public build identity');
  requireThat(typeof record.allocationKey === 'string' && record.allocationKey.length === 64 &&
    /^[a-f0-9]{64}$/.test(record.allocationKey),
    'Invalid frontend allocation identity');
  return {
    frontendIdentity: JSON.stringify({
      ...Object.fromEntries(fields.map(field => [field, projection[field]])),
      allocationIdentity: record.allocationKey,
    }),
    props: `<Project>
  <PropertyGroup>
    <Version>${record.canonicalVersion}</Version>
    <AssemblyVersion>${record.baseVersion}.0</AssemblyVersion>
    <FileVersion>${record.baseVersion}.0</FileVersion>
    <InformationalVersion>${record.canonicalVersion}+sha.${record.sourceCommit}</InformationalVersion>
    <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
  </PropertyGroup>
  <ItemGroup>
${fields.map(field => `    <AssemblyMetadata Include="${field}" Value="${escape(record[field])}" />`).join('\n')}
    <AssemblyMetadata Include="identitySha256" Value="${hash(record)}" />
  </ItemGroup>
</Project>
`,
    frontend: JSON.stringify({ service: 'frontend', commit: record.sourceCommit,
      ...projection }),
    labels: Object.entries(identityLabels(record)).map(([key, value]) => `${key}=${value}`).join('\n'),
  };
}

export function emitBuildIdentity(record, root = '.') {
  const metadata = buildMetadata(record);
  mkdirSync(join(root, 'src', 'Web', 'ReactApp', 'public'), { recursive: true });
  writeFileSync(join(root, 'src', 'ReleaseIdentity.props'), metadata.props);
  writeFileSync(join(root, 'src', 'Web', 'ReactApp', 'public', 'release-identity.json'), metadata.frontend);
  writeFileSync(join(root, 'release-identity.json'), JSON.stringify(publicAuthorization(record)));
  writeFileSync(join(root, 'release-labels.txt'), metadata.labels);
  return metadata;
}
