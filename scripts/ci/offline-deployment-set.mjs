// Release-bound offline deployment set (issue #3081).
//
// The release workflow generates offline-deployment-set.json from the source checkout: the exact
// bytes of every deployment template a supported offline topology needs (compose templates, the
// entrypoint/security configuration and the nginx configurations), the configuration schema those
// templates consume, the approved host tools pinned by SHA-256 and size (from
// scripts/docker/offline-tools.lock.json), the tools that ship inside pinned infrastructure images,
// and the fixed supported topologies. It is bound to the release identity and signed with the same
// keyless workflow identity as update-manifest.json.
//
// Everything the document contains is either a code constant here (template allowlist, topologies)
// or a value it carries itself (template bytes, tool pins). Validation therefore regenerates the
// document from the carried values and requires byte equality: a non-canonical, reordered,
// extended or partially edited document fails closed. The signature is the only trust source for
// the carried values; the offline bundle verifier additionally binds topology images to the signed
// manifest and infrastructure image list and every tool member to its signed pin. The document is
// never rollout authorization.
import { createHash } from 'node:crypto';
import { closeSync, constants, fstatSync, lstatSync, openSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { components, requireThat } from './release-policy.mjs';

export const deploymentSetName = 'offline-deployment-set.json';
export const deploymentSetSignatureName = 'offline-deployment-set.sigstore.json';
export const deploymentSetKind = 'printfarmer-offline-deployment-set';
export const offlineToolsLockKind = 'printfarmer-offline-tools-lock';
export const offlineToolsLockPath = 'scripts/docker/offline-tools.lock.json';
export const configSchemaVersion = 1;

const identityKeys = ['tag', 'version', 'channel', 'sourceBranch', 'sourceCommit', 'buildId', 'sequence'];
const sha256Pattern = /^[a-f0-9]{64}$/;
const toolNamePattern = /^[a-z][a-z0-9_-]{0,31}$/;
const artifactNamePattern = /^[A-Za-z0-9][A-Za-z0-9._+-]{0,94}$/;
const versionPattern = /^[0-9]+\.[0-9]+\.[0-9]+$/;
const artifactPlatforms = ['linux/amd64', 'linux/arm64', 'windows/amd64'];
const imageToolPathPattern = /^\/(?:[A-Za-z0-9._-]+\/)*[A-Za-z0-9._-]+$/;
const infrastructureIdPattern = /^[a-z][a-z0-9-]{0,39}$/;
const maxToolBytes = 512 * 1024 * 1024;
const maxTemplateBytes = 256 * 1024;
export const toolMemberPrefix = 'tool-';
export const toolMember = name => `${toolMemberPrefix}${name}`;

const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const exactKeys = (value, keys) => value && typeof value === 'object' && !Array.isArray(value) &&
  Object.keys(value).sort().join() === [...keys].sort().join();
const byString = (a, b) => (a < b ? -1 : a > b ? 1 : 0);

// Supported-topology templates only. Optional add-ons (monitoring, registry, emulators, pgAdmin,
// Spoolman, go2rtc, Obico ML, telemetry) are outside offline support and are never carried.
const commonTemplates = [
  'deploy/nginx/conf.d/frontend-app.conf',
  'deploy/nginx/nginx-frontend.conf',
  'deploy/nginx/nginx-proxy-split.conf',
  'deploy/nginx/nginx-proxy.conf',
  'deploy/nginx/nginx.conf',
  'scripts/docker/compose-templates/docker-compose.common.yml',
  'scripts/docker/configs/docker-entrypoint-config.sh',
  'scripts/docker/configs/security-config.json',
];
const compose = name => `scripts/docker/compose-templates/docker-compose.${name}.yml`;
const splitTemplates = ['scripts/docker/compose-templates/docker-compose.yml', compose('discovery'),
  compose('orcaslicer-worker'), compose('slicer-host')];
const monolithTemplates = [compose('monolith'), compose('orcaslicer-worker')];
const databases = {
  postgres: { image: 'postgres', template: compose('database.postgres'), tools: ['pg_dump', 'pg_restore'] },
  sqlserver: { image: 'mssql', template: compose('database.sqlserver'), tools: ['sqlcmd'] },
};

// Host prerequisite, never bundled: the update engine drives Docker Engine through its own CLI.
export const hostPrerequisites = Object.freeze(['docker']);

function topologyDefinitions() {
  const topologies = {};
  for (const [database, { image, template, tools }] of Object.entries(databases)) {
    topologies[`monolith-${database}`] = {
      images: ['monolith', 'orcaslicer-worker', image].sort(byString),
      templates: [...commonTemplates, ...monolithTemplates, template].sort(byString),
      tools: ['cosign', ...tools].sort(byString),
    };
    topologies[`split-${database}`] = {
      images: ['api', 'frontend', 'nginx', 'orcaslicer-worker', 'printer-discovery', 'slicer-host', image].sort(byString),
      templates: [...commonTemplates, ...splitTemplates, template].sort(byString),
      tools: ['cosign', ...tools].sort(byString),
    };
  }
  return Object.fromEntries(Object.keys(topologies).sort(byString).map(name => [name, topologies[name]]));
}

export const deploymentTopologies = Object.freeze(topologyDefinitions());
export const deploymentTemplatePaths = Object.freeze([...new Set(Object.values(deploymentTopologies)
  .flatMap(topology => topology.templates))].sort(byString));

// ---------------------------------------------------------------------------------------------
// Approved tools lock
// ---------------------------------------------------------------------------------------------
function validateArtifacts(artifacts, label) {
  requireThat(Array.isArray(artifacts) && artifacts.length > 0 && artifacts.length <= 8, `${label} artifact list is invalid`);
  let previous = '';
  for (const artifact of artifacts) {
    requireThat(exactKeys(artifact, ['tool', 'version', 'platform', 'name', 'url', 'size', 'sha256']) &&
      typeof artifact.name === 'string' && artifactNamePattern.test(artifact.name) && !artifact.name.endsWith('.') &&
      artifact.name > previous, `${label} artifact entry is invalid or unsorted`);
    previous = artifact.name;
    requireThat(toolNamePattern.test(artifact.tool ?? ''), `${label} artifact tool is invalid: ${artifact.name}`);
    requireThat(versionPattern.test(artifact.version ?? ''), `${label} artifact version is invalid: ${artifact.name}`);
    requireThat(artifactPlatforms.includes(artifact.platform), `${label} artifact platform is invalid: ${artifact.name}`);
    requireThat(typeof artifact.url === 'string' && artifact.url ===
      `https://github.com/sigstore/${artifact.tool}/releases/download/v${artifact.version}/${artifact.name}`,
    `${label} artifact URL is not the pinned upstream release asset: ${artifact.name}`);
    requireThat(Number.isSafeInteger(artifact.size) && artifact.size > 0 && artifact.size <= maxToolBytes,
      `${label} artifact size is invalid: ${artifact.name}`);
    requireThat(sha256Pattern.test(artifact.sha256 ?? ''), `${label} artifact SHA-256 is invalid: ${artifact.name}`);
  }
  requireThat(new Set(artifacts.map(artifact => `${artifact.tool}/${artifact.platform}`)).size === artifacts.length,
    `${label} pins a tool platform more than once`);
  return artifacts;
}

function validateImageTools(imageTools, label) {
  requireThat(Array.isArray(imageTools) && imageTools.length > 0 && imageTools.length <= 8, `${label} image tool list is invalid`);
  let previous = '';
  for (const entry of imageTools) {
    requireThat(exactKeys(entry, ['tool', 'image', 'path']) && toolNamePattern.test(entry.tool ?? '') &&
      entry.tool > previous, `${label} image tool entry is invalid or unsorted`);
    previous = entry.tool;
    requireThat(infrastructureIdPattern.test(entry.image ?? ''), `${label} image tool image is invalid: ${entry.tool}`);
    requireThat(typeof entry.path === 'string' && imageToolPathPattern.test(entry.path) && !entry.path.includes('/../') &&
      !entry.path.includes('/./'), `${label} image tool path is invalid: ${entry.tool}`);
  }
  return imageTools;
}

// Every tool a topology names must be approved exactly once: bundled by pin or shipped in an image.
function requireToolCoverage(artifacts, imageTools, label) {
  const bundled = new Set(artifacts.map(artifact => artifact.tool));
  for (const entry of imageTools) {
    requireThat(!bundled.has(entry.tool), `${label} approves ${entry.tool} both as a bundled and an image tool`);
  }
  const approved = new Set([...bundled, ...imageTools.map(entry => entry.tool)]);
  const named = new Set(Object.values(deploymentTopologies).flatMap(topology => topology.tools));
  requireThat([...named].every(tool => approved.has(tool)) && [...approved].every(tool => named.has(tool)),
    `${label} does not approve exactly the tools the supported topologies require`);
  for (const [database, { image, tools }] of Object.entries(databases)) {
    for (const tool of tools) {
      requireThat(imageTools.some(entry => entry.tool === tool && entry.image === image),
        `${label} does not take ${tool} from the pinned ${image} image (${database})`);
    }
  }
}

export function validateOfflineToolsLock(bytes) {
  let lock;
  try {
    lock = JSON.parse(Buffer.from(bytes).toString('utf8'));
  } catch {
    throw new Error('Offline tools lock is not valid JSON');
  }
  requireThat(exactKeys(lock, ['schema', 'kind', 'artifacts', 'imageTools']) && lock.schema === 1 &&
    lock.kind === offlineToolsLockKind, 'Offline tools lock schema is not supported');
  validateArtifacts(lock.artifacts, 'Offline tools lock');
  validateImageTools(lock.imageTools, 'Offline tools lock');
  requireToolCoverage(lock.artifacts, lock.imageTools, 'Offline tools lock');
  return lock;
}

// ---------------------------------------------------------------------------------------------
// Templates and configuration schema
// ---------------------------------------------------------------------------------------------
// `${NAME}`, `${NAME:-default}`, `${NAME?err}` and friends; `$${NAME}` is a compose literal.
const variablePattern = /(?<!\$)\$\{([A-Za-z_][A-Za-z0-9_]*)(?:[:]?[-?+=][^}]*)?\}/g;

export function configSchemaVariables(templates) {
  const names = new Set();
  for (const bytes of templates.values()) {
    for (const match of Buffer.from(bytes).toString('utf8').matchAll(variablePattern)) names.add(match[1]);
  }
  return [...names].sort(byString);
}

// Release time only: reads the allowlisted templates as regular files from the source checkout. The file is
// opened once and checked through its descriptor, so the bytes read are the bytes that were checked.
export function readDeploymentTemplates(source) {
  return new Map(deploymentTemplatePaths.map(path => {
    const full = join(source, ...path.split('/'));
    let fd;
    try {
      fd = openSync(full, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
    } catch {
      throw new Error(`Deployment template is missing or not a regular file: ${path}`);
    }
    try {
      const stat = fstatSync(fd);
      const link = lstatSync(full, { throwIfNoEntry: false });
      requireThat(stat.isFile() && link?.isFile() && !link.isSymbolicLink() && link.ino === stat.ino && link.dev === stat.dev,
        `Deployment template is missing or not a regular file: ${path}`);
      requireThat(stat.size > 0 && stat.size <= maxTemplateBytes, `Deployment template size is invalid: ${path}`);
      const bytes = readFileSync(fd);
      requireThat(bytes.length === stat.size, `Deployment template changed while it was read: ${path}`);
      return [path, bytes];
    } finally {
      closeSync(fd);
    }
  }));
}

// ---------------------------------------------------------------------------------------------
// The signed release asset
// ---------------------------------------------------------------------------------------------
export function deploymentSetDocument(identity, { templates, lock }) {
  requireThat(exactKeys(identity, identityKeys) && identityKeys.every(key => identity[key] !== undefined &&
    identity[key] !== null), 'Offline deployment set requires a complete release identity');
  requireThat(['stable', 'insider'].includes(identity.channel), 'Offline deployment set channel must be stable or insider');
  requireThat(templates instanceof Map && templates.size === deploymentTemplatePaths.length &&
    deploymentTemplatePaths.every(path => templates.has(path)),
  'Offline deployment set requires exactly the supported deployment templates');
  const artifacts = validateArtifacts(lock?.artifacts, 'Offline deployment set');
  const imageTools = validateImageTools(lock?.imageTools, 'Offline deployment set');
  requireToolCoverage(artifacts, imageTools, 'Offline deployment set');
  const files = deploymentTemplatePaths.map(path => {
    const bytes = Buffer.from(templates.get(path));
    requireThat(bytes.length > 0 && bytes.length <= maxTemplateBytes, `Deployment template size is invalid: ${path}`);
    return { path, size: bytes.length, sha256: sha256(bytes), content: bytes.toString('base64') };
  });
  return Buffer.from(`${JSON.stringify({
    schema: 1,
    kind: deploymentSetKind,
    release: Object.fromEntries(identityKeys.map(key => [key, identity[key]])),
    configSchema: { version: configSchemaVersion, variables: configSchemaVariables(templates) },
    templates: files,
    tools: artifacts.map(artifact => ({ tool: artifact.tool, version: artifact.version, platform: artifact.platform,
      name: artifact.name, url: artifact.url, size: artifact.size, sha256: artifact.sha256 })),
    imageTools: imageTools.map(entry => ({ tool: entry.tool, image: entry.image, path: entry.path })),
    hostPrerequisites,
    topologies: deploymentTopologies,
    rolloutAuthorization: false,
  }, undefined, 2)}\n`);
}

// Offline: regenerates the document from its own carried values and requires byte equality, so a
// document that is not exactly what the release workflow would produce for this identity fails.
export function validateDeploymentSet(bytes, identity) {
  requireThat(Buffer.isBuffer(bytes) || bytes instanceof Uint8Array, 'Offline deployment set must be bytes');
  let document;
  try {
    document = JSON.parse(Buffer.from(bytes).toString('utf8'));
  } catch {
    throw new Error('Offline deployment set is not valid JSON');
  }
  requireThat(document && typeof document === 'object' && !Array.isArray(document) && Array.isArray(document.templates) &&
    document.templates.length === deploymentTemplatePaths.length &&
    document.templates.every(file => file && typeof file.path === 'string' && typeof file.content === 'string' &&
      /^[A-Za-z0-9+/]*={0,2}$/.test(file.content)),
  'Offline deployment set does not carry exactly the supported deployment templates');
  const templates = new Map(document.templates.map(file => [file.path, Buffer.from(file.content, 'base64')]));
  let expected;
  try {
    expected = deploymentSetDocument(identity, { templates, lock: { artifacts: document.tools, imageTools: document.imageTools } });
  } catch (error) {
    throw new Error(`Offline deployment set is invalid: ${error.message}`);
  }
  requireThat(Buffer.from(bytes).equals(expected),
    'Offline deployment set is not the exact release-bound deployment set for this release identity');
  return JSON.parse(expected.toString('utf8'));
}

// Offline bundle binding: every topology image is a release-selected image, the topologies cover
// the complete carried image set, and every image tool comes from a pinned infrastructure image.
export function bindDeploymentSetToImages(document, requiredImageIds, infrastructureIds) {
  const required = new Set(requiredImageIds);
  const infrastructure = new Set(infrastructureIds);
  const covered = new Set();
  for (const [name, topology] of Object.entries(document.topologies)) {
    for (const id of topology.images) {
      requireThat(required.has(id), `Offline deployment set topology ${name} names an image the release does not select: ${id}`);
      covered.add(id);
    }
  }
  requireThat(covered.size === required.size,
    'Offline deployment set topologies do not cover exactly the release-selected image set');
  for (const entry of document.imageTools) {
    requireThat(infrastructure.has(entry.image),
      `Offline deployment set image tool ${entry.tool} is not in a pinned infrastructure image: ${entry.image}`);
  }
  requireThat(Object.values(document.topologies).every(topology =>
    topology.images.every(id => Object.hasOwn(components, id) || infrastructure.has(id))),
  'Offline deployment set topology names an image outside the release components and infrastructure');
  return document;
}
