import { createHash } from 'node:crypto';
import { access, open, readFile, readdir } from 'node:fs/promises';
import path from 'node:path';

const skippedDirectories = new Set([
  '.beads',
  '.git',
  'bin',
  'node_modules',
  'obj',
]);

const textExtensions = new Set([
  '.cs',
  '.csproj',
  '.css',
  '.html',
  '.js',
  '.json',
  '.jsx',
  '.md',
  '.mjs',
  '.props',
  '.ps1',
  '.sh',
  '.sql',
  '.targets',
  '.ts',
  '.tsx',
  '.txt',
  '.yaml',
  '.yml',
]);

export function normalizeRelativePath(value) {
  return value.replaceAll('\\', '/').replace(/^\.\//, '');
}

export function normalizedLicenseText(value) {
  return `${value.replaceAll('\r\n', '\n').trimEnd()}\n`;
}

export function sha256(value) {
  return createHash('sha256').update(value).digest('hex');
}

function normalizedLicenseSha256(value) {
  const text = Buffer.isBuffer(value) ? value.toString('utf8') : value;
  return sha256(normalizedLicenseText(text));
}

export function createError(code, filePath, message) {
  return { code, path: normalizeRelativePath(filePath), message };
}

export async function pathExists(filePath) {
  try {
    await access(filePath);
    return true;
  } catch {
    return false;
  }
}

function isAbsoluteHttpsUrl(value) {
  try {
    const url = new URL(value);
    return url.protocol === 'https:'
      && Boolean(url.hostname)
      && !url.username
      && !url.password;
  } catch {
    return false;
  }
}

export async function readJson(filePath) {
  return JSON.parse(await readFile(filePath, 'utf8'));
}

const xmlEntityDecodeMap = {
  '&amp;': '&',
  '&apos;': "'",
  '&gt;': '>',
  '&lt;': '<',
  '&quot;': '"',
};

// Decodes all XML entities in a single pass so a decoded `&amp;` (which
// produces a literal `&`) is never re-scanned and mistaken for the start of
// another entity (e.g. `&amp;lt;` must decode to the literal text `&lt;`,
// not further to `<`). Decoding sequentially with separate replaceAll calls
// double-unescapes such values.
export function decodeXml(value) {
  return value
    .replace(/&amp;|&apos;|&gt;|&lt;|&quot;/g, (entity) => xmlEntityDecodeMap[entity])
    .trim();
}

function isIsoDate(value) {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) {
    return false;
  }

  return !Number.isNaN(Date.parse(`${value}T00:00:00Z`));
}

function parseSemanticVersion(value) {
  const match = /^v?(\d+)\.(\d+)\.(\d+)(?:[-+][0-9A-Za-z.-]+)?$/.exec(value ?? '');
  return match ? match.slice(1, 4).map(Number) : undefined;
}

function compareSemanticVersions(left, right) {
  for (let index = 0; index < left.length; index += 1) {
    if (left[index] !== right[index]) {
      return left[index] - right[index];
    }
  }

  return 0;
}

async function walkFiles(rootPath, relativePath = '', excludedDirectories = skippedDirectories) {
  const absolutePath = path.join(rootPath, relativePath);
  if (!(await pathExists(absolutePath))) {
    return [];
  }

  const entries = await readdir(absolutePath, { withFileTypes: true });
  const files = [];

  for (const entry of entries) {
    if (entry.isDirectory() && excludedDirectories.has(entry.name)) {
      continue;
    }

    const childRelativePath = normalizeRelativePath(path.join(relativePath, entry.name));
    if (entry.isDirectory()) {
      files.push(...await walkFiles(rootPath, childRelativePath, excludedDirectories));
    } else if (entry.isFile()) {
      files.push(childRelativePath);
    }
  }

  return files;
}

function packageNameFromLockPath(lockPath, metadata) {
  if (typeof metadata.name === 'string' && metadata.name.length > 0) {
    return metadata.name;
  }

  if (lockPath.length === 0) {
    return '<root>';
  }

  const marker = 'node_modules/';
  const markerIndex = lockPath.lastIndexOf(marker);
  if (markerIndex < 0) {
    return lockPath;
  }

  return lockPath.slice(markerIndex + marker.length);
}

function npmPurl(packageName, version) {
  const segments = packageName.startsWith('@')
    ? packageName.split('/', 2).map(encodeURIComponent)
    : [encodeURIComponent(packageName)];
  return `pkg:npm/${segments.join('/')}@${encodeURIComponent(version)}`;
}

export function productionNpmPackagesFromLock(lock) {
  return Object.entries(lock.packages ?? {})
    .filter(([lockPackagePath, metadata]) =>
      lockPackagePath.length > 0
      && metadata.link !== true
      && metadata.dev !== true
      && metadata.optional !== true)
    .map(([lockPackagePath, metadata]) => ({
      integrity: metadata.integrity,
      license: metadata.license ?? 'MISSING',
      lockPath: normalizeRelativePath(lockPackagePath),
      name: packageNameFromLockPath(lockPackagePath, metadata),
      resolved: metadata.resolved,
      version: metadata.version ?? lock.version ?? 'unknown',
    }))
    .sort((left, right) =>
      left.name.localeCompare(right.name)
      || left.version.localeCompare(right.version)
      || left.lockPath.localeCompare(right.lockPath));
}

function findLicenseException(policy, ecosystem, packageName, version, observedLicense) {
  return policy.reviewedExceptions.find((exception) =>
    exception.ecosystem === ecosystem
    && exception.package.toLowerCase() === packageName.toLowerCase()
    && exception.version === version
    && exception.observedLicense === observedLicense);
}

function findReviewedEvidence(policy, ecosystem, observedLicense) {
  return (policy.reviewedEvidence ?? []).find((evidence) =>
    evidence.ecosystem === ecosystem
    && evidence.observedLicense === observedLicense);
}

function findLicenseApproval(policy, ecosystem, packageName, version, observedLicense) {
  return findLicenseException(policy, ecosystem, packageName, version, observedLicense)
    ?? findReviewedEvidence(policy, ecosystem, observedLicense);
}

function resolveApprovedLicense(policy, ecosystem, packageName, version, observedLicense) {
  if (policy.allowedExpressions.includes(observedLicense)) {
    return observedLicense;
  }

  return findLicenseApproval(policy, ecosystem, packageName, version, observedLicense)
    ?.approvedExpression;
}

function validateException(exception, contextPath) {
  const errors = [];
  if (!exception.evidence || !exception.reviewer || !exception.rationale) {
    errors.push(createError('LICENSE_EXCEPTION_INCOMPLETE', contextPath, 'exception requires evidence, reviewer, and rationale'));
  }

  if (!isIsoDate(exception.reviewDate) || !isIsoDate(exception.reviewAfter)) {
    errors.push(createError('LICENSE_EXCEPTION_DATE', contextPath, 'exception dates must be YYYY-MM-DD'));
  }

  if (isIsoDate(exception.reviewAfter) && Date.parse(`${exception.reviewAfter}T23:59:59Z`) < Date.now()) {
    errors.push(createError('LICENSE_EXCEPTION_EXPIRED', contextPath, `exception expired on ${exception.reviewAfter}`));
  }

  return errors;
}

function validateObservedLicense(policy, ecosystem, packageName, version, observedLicense, contextPath) {
  if (policy.allowedExpressions.includes(observedLicense)) {
    return [];
  }

  const approval = findLicenseApproval(policy, ecosystem, packageName, version, observedLicense);
  if (approval) {
    const errors = validateException(approval, contextPath);
    if (!policy.allowedExpressions.includes(approval.approvedExpression)) {
      errors.push(createError(
        'LICENSE_EXCEPTION_EXPRESSION',
        contextPath,
        `approved expression ${approval.approvedExpression} is not in allowedExpressions`,
      ));
    }

    return errors;
  }

  const isDenied = policy.deniedValues.includes(observedLicense);
  return [createError(
    isDenied ? 'LICENSE_UNKNOWN' : 'LICENSE_UNREVIEWED',
    contextPath,
    `${ecosystem} package ${packageName}@${version} has unapproved license "${observedLicense}"`,
  )];
}

async function validateNpmLicenses(repoRoot, policy) {
  const errors = [];

  for (const lockRelativePath of policy.npmLockFiles) {
    const lockPath = path.join(repoRoot, lockRelativePath);
    if (!(await pathExists(lockPath))) {
      errors.push(createError('NPM_LOCK_MISSING', lockRelativePath, 'required npm lockfile is missing'));
      continue;
    }

    const lock = await readJson(lockPath);
    for (const [lockPackagePath, metadata] of Object.entries(lock.packages ?? {})) {
      if (metadata.link === true) {
        continue;
      }

      const packageName = packageNameFromLockPath(lockPackagePath, metadata);
      const version = metadata.version ?? lock.version ?? 'unknown';
      const observedLicense = metadata.license ?? 'MISSING';
      errors.push(...validateObservedLicense(
        policy,
        'npm',
        packageName,
        version,
        observedLicense,
        `${lockRelativePath}:${lockPackagePath || '<root>'}`,
      ));
    }
  }

  return errors;
}

export async function createNpmLicenseInventory(repoRoot, policy) {
  const errors = [];
  const lockRelativePath = policy.sbom?.npmBundleLockFile;
  if (!lockRelativePath) {
    return {
      errors: [createError(
        'NPM_BUNDLE_LOCK_MISSING',
        'compliance/dependency-license-policy.json',
        'sbom.npmBundleLockFile is required',
      )],
      packages: [],
    };
  }

  const lockPath = path.join(repoRoot, lockRelativePath);
  if (!(await pathExists(lockPath))) {
    return {
      errors: [createError('NPM_LOCK_MISSING', lockRelativePath, 'bundled frontend lockfile is missing')],
      packages: [],
    };
  }

  const packages = new Map();
  const lock = await readJson(lockPath);
  for (const packageRecord of productionNpmPackagesFromLock(lock)) {
    const contextPath = `${lockRelativePath}:${packageRecord.lockPath}`;
    errors.push(...validateObservedLicense(
      policy,
      'npm',
      packageRecord.name,
      packageRecord.version,
      packageRecord.license,
      contextPath,
    ));
    const licenseExpression = resolveApprovedLicense(
      policy,
      'npm',
      packageRecord.name,
      packageRecord.version,
      packageRecord.license,
    ) ?? packageRecord.license;
    const purl = npmPurl(packageRecord.name, packageRecord.version);
    packages.set(purl, {
      integrity: packageRecord.integrity,
      licenseExpression,
      name: packageRecord.name,
      purl,
      resolved: packageRecord.resolved,
      version: packageRecord.version,
    });
  }

  for (const fallback of policy.npm?.licenseTextFallbacks ?? []) {
    const contextPath = `compliance/dependency-license-policy.json:npm:${fallback.package}@${fallback.version}`;
    errors.push(...validateException(fallback, contextPath));
    if (!isAbsoluteHttpsUrl(fallback.source)) {
      errors.push(createError('LICENSE_POLICY_INVALID', contextPath, 'source must be an absolute HTTPS URL'));
    }
    const matchingPackage = [...packages.values()].find((record) =>
      record.name === fallback.package
      && record.version === fallback.version
      && record.licenseExpression === fallback.license);
    if (!matchingPackage) {
      errors.push(createError(
        'LICENSE_POLICY_STALE',
        contextPath,
        'reviewed npm license fallback does not match a production lock entry',
      ));
    }

    const licensePath = path.resolve(repoRoot, fallback.licenseFile ?? '');
    const relativeLicensePath = path.relative(repoRoot, licensePath);
    if (relativeLicensePath.startsWith('..') || path.isAbsolute(relativeLicensePath)) {
      errors.push(createError('LICENSE_POLICY_INVALID', contextPath, 'licenseFile escapes the repository'));
      continue;
    }
    try {
      const licenseBytes = await readFile(licensePath);
      const digest = normalizedLicenseSha256(licenseBytes);
      if (digest !== fallback.sha256) {
        errors.push(createError(
          'LICENSE_EVIDENCE_MISMATCH',
          contextPath,
          `license file SHA-256 ${digest} does not match reviewed ${fallback.sha256}`,
        ));
      }
      if (licenseBytes.toString('utf8').trim().length === 0) {
        errors.push(createError('LICENSE_EVIDENCE_MISSING', contextPath, 'license file is empty'));
      }
    } catch (error) {
      errors.push(createError(
        'LICENSE_EVIDENCE_MISSING',
        contextPath,
        `cannot read ${fallback.licenseFile}: ${error.message}`,
      ));
    }
  }

  return {
    errors,
    packages: [...packages.values()]
      .sort((left, right) =>
        left.name.localeCompare(right.name) || left.version.localeCompare(right.version)),
  };
}

export async function findNugetAssetsFiles(rootPath) {
  const excludedDirectories = new Set(skippedDirectories);
  excludedDirectories.delete('obj');
  return (await walkFiles(rootPath, '', excludedDirectories))
    .filter((filePath) => filePath.endsWith('/obj/project.assets.json') || filePath === 'obj/project.assets.json');
}

async function readNugetLicense(packagePath, packageName) {
  const nuspecPath = path.join(packagePath, `${packageName.toLowerCase()}.nuspec`);
  if (!(await pathExists(nuspecPath))) {
    return { observedLicense: 'MISSING' };
  }

  const nuspec = await readFile(nuspecPath, 'utf8');
  const expressionMatch = nuspec.match(/<license\b[^>]*type=["']expression["'][^>]*>([^<]+)<\/license>/i);
  if (expressionMatch) {
    return { observedLicense: decodeXml(expressionMatch[1]) };
  }

  const fileMatch = nuspec.match(/<license\b[^>]*type=["']file["'][^>]*>([^<]+)<\/license>/i);
  if (fileMatch) {
    const licenseSegments = decodeXml(fileMatch[1]).split(/[\\/]+/);
    const licensePath = path.resolve(packagePath, ...licenseSegments);
    const relativeLicensePath = path.relative(packagePath, licensePath);
    if (relativeLicensePath.startsWith('..') || path.isAbsolute(relativeLicensePath) || !(await pathExists(licensePath))) {
      return { observedLicense: 'MISSING' };
    }

    const licenseContent = await readFile(licensePath);
    return {
      licenseText: licenseContent.toString('utf8'),
      observedLicense: `FILE-SHA256:${sha256(licenseContent)}`,
    };
  }

  const licenseUrlMatch = nuspec.match(/<licenseUrl\b[^>]*>([^<]+)<\/licenseUrl>/i);
  if (licenseUrlMatch) {
    return { observedLicense: `URL:${decodeXml(licenseUrlMatch[1])}` };
  }

  return { observedLicense: 'MISSING' };
}

export async function createNugetLicenseInventory(repoRoot, policy) {
  const errors = [];
  const assetsRoot = path.join(repoRoot, policy.nuget.assetsRoot);
  const assetsFiles = await findNugetAssetsFiles(assetsRoot);
  if (assetsFiles.length === 0) {
    errors.push(createError(
      'NUGET_ASSETS_MISSING',
      policy.nuget.assetsRoot,
      'no project.assets.json files found; run dotnet restore before dependency validation',
    ));
    return {
      errors,
      inventory: {
        schemaVersion: 1,
        packages: [],
        projects: [],
      },
    };
  }

  const packages = new Map();
  const projects = new Map();
  const reviewedPackages = new Set();
  for (const assetsRelativePath of assetsFiles) {
    const assetsPath = path.join(assetsRoot, assetsRelativePath);
    const assets = await readJson(assetsPath);
    const projectPath = normalizeRelativePath(assets.project?.restore?.projectPath ?? '');
    const normalizedProjectPath = `/${projectPath.toLowerCase()}/`;

    if (policy.nuget.excludedProjectPathSegments.some((segment) =>
      normalizedProjectPath.includes(segment.toLowerCase()))) {
      continue;
    }

    const projectName = assets.project?.restore?.projectName
      ?? path.basename(projectPath, path.extname(projectPath));
    if (projectName) {
      projects.set(projectName.toLowerCase(), {
        name: projectName,
        path: projectPath,
      });
    }

    const packageFolders = Object.keys(assets.packageFolders ?? {});
    for (const [libraryKey, libraryMetadata] of Object.entries(assets.libraries ?? {})) {
      if (libraryMetadata.type !== 'package' || reviewedPackages.has(libraryKey.toLowerCase())) {
        continue;
      }

      reviewedPackages.add(libraryKey.toLowerCase());
      const separatorIndex = libraryKey.lastIndexOf('/');
      if (separatorIndex < 1) {
        errors.push(createError('NUGET_IDENTITY', assetsRelativePath, `invalid package identity ${libraryKey}`));
        continue;
      }

      const packageName = libraryKey.slice(0, separatorIndex);
      const version = libraryKey.slice(separatorIndex + 1);
      let observedLicense = 'MISSING';
      let licenseText;
      let resolvedPackagePath;

      for (const packageFolder of packageFolders) {
        const packagePath = path.join(packageFolder, packageName.toLowerCase(), version);
        if (await pathExists(packagePath)) {
          const evidence = await readNugetLicense(packagePath, packageName);
          observedLicense = evidence.observedLicense;
          licenseText = evidence.licenseText;
          resolvedPackagePath = packagePath;
          break;
        }
      }

      const contextPath = `${projectPath || assetsRelativePath}:${libraryKey}`;
      const licenseErrors = validateObservedLicense(
        policy,
        'nuget',
        packageName,
        version,
        observedLicense,
        contextPath,
      );
      errors.push(...licenseErrors);

      const licenseExpression = resolveApprovedLicense(
        policy,
        'nuget',
        packageName,
        version,
        observedLicense,
      ) ?? observedLicense;
      if (/LicenseRef-[A-Za-z0-9.-]+/.test(licenseExpression) && !licenseText) {
        errors.push(createError(
          'NUGET_CUSTOM_LICENSE_TEXT',
          contextPath,
          `custom license ${licenseExpression} requires bundled license text`,
        ));
      }

      packages.set(libraryKey.toLowerCase(), {
        files: (libraryMetadata.files ?? []).map(normalizeRelativePath).sort(),
        licenseExpression,
        ...(licenseText && /LicenseRef-[A-Za-z0-9.-]+/.test(licenseExpression)
          ? { licenseText }
          : {}),
        name: packageName,
        observedLicense,
        packagePath: resolvedPackagePath,
        purl: `pkg:nuget/${encodeURIComponent(packageName)}@${encodeURIComponent(version)}`,
        version,
      });
    }
  }

  return {
    errors,
    inventory: {
      schemaVersion: 1,
      packages: [...packages.values()]
        .map(({ packagePath: _, ...packageRecord }) => packageRecord)
        .sort((left, right) =>
          left.name.localeCompare(right.name) || left.version.localeCompare(right.version)),
      projects: [...projects.values()]
        .sort((left, right) => left.name.localeCompare(right.name)),
    },
  };
}

async function validateNugetLicenses(repoRoot, policy) {
  return (await createNugetLicenseInventory(repoRoot, policy)).errors;
}

export async function validateDependencyLicenses(repoRoot, dependencyPolicy) {
  const errors = [];
  for (const exception of dependencyPolicy.reviewedExceptions) {
    errors.push(...validateException(
      exception,
      `compliance/dependency-license-policy.json:${exception.ecosystem}:${exception.package}@${exception.version}`,
    ));
  }

  for (const evidence of dependencyPolicy.reviewedEvidence ?? []) {
    const contextPath = `compliance/dependency-license-policy.json:${evidence.ecosystem}:${evidence.observedLicense}`;
    errors.push(...validateException(evidence, contextPath));
    if (!dependencyPolicy.allowedExpressions.includes(evidence.approvedExpression)) {
      errors.push(createError(
        'LICENSE_EXCEPTION_EXPRESSION',
        contextPath,
        `approved expression ${evidence.approvedExpression} is not in allowedExpressions`,
      ));
    }
  }

  const sbomReviews = [
    ...(dependencyPolicy.sbom?.reviewedEcosystems ?? []),
    ...(dependencyPolicy.sbom?.runtimePackageEvidence ?? []),
    ...(dependencyPolicy.sbom?.packageEvidence ?? []),
  ];
  for (const review of sbomReviews) {
    const contextPath = 'compliance/dependency-license-policy.json:sbom';
    errors.push(...validateException(review, contextPath));
    if (review.approvedExpression
      && !dependencyPolicy.allowedExpressions.includes(review.approvedExpression)) {
      errors.push(createError(
        'LICENSE_EXCEPTION_EXPRESSION',
        contextPath,
        `approved expression ${review.approvedExpression} is not in allowedExpressions`,
      ));
    }
  }

  errors.push(...await validateNpmLicenses(repoRoot, dependencyPolicy));
  errors.push(...await validateNugetLicenses(repoRoot, dependencyPolicy));
  return errors;
}

function isSafeRepositoryPath(relativePath) {
  const normalized = normalizeRelativePath(relativePath);
  return normalized.length > 0
    && !path.isAbsolute(relativePath)
    && normalized !== '..'
    && !normalized.startsWith('../')
    && !normalized.includes('/../');
}

function validateExceptionRecord(exception, contextPath) {
  const errors = [];
  for (const property of ['id', 'scope', 'rationale', 'approver']) {
    if (typeof exception[property] !== 'string' || exception[property].trim().length === 0) {
      errors.push(createError('PROVENANCE_EXCEPTION', contextPath, `${property} is required`));
    }
  }

  if (!isIsoDate(exception.approvalDate) || !isIsoDate(exception.reviewAfter)) {
    errors.push(createError('PROVENANCE_EXCEPTION_DATE', contextPath, 'approvalDate and reviewAfter must be YYYY-MM-DD'));
  }

  if (exception.licenseCompatibility !== 'compatible') {
    errors.push(createError('PROVENANCE_EXCEPTION_LICENSE', contextPath, 'exceptions cannot bypass incompatible licensing'));
  }

  return errors;
}

export async function validateLicenseMetadata(repoRoot, licensingPolicy) {
  const errors = [];
  const canonicalPath = path.join(repoRoot, licensingPolicy.canonicalLicense.path);
  if (!(await pathExists(canonicalPath))) {
    errors.push(createError('LICENSE_MISSING', licensingPolicy.canonicalLicense.path, 'canonical license file is missing'));
  } else {
    const license = normalizedLicenseText(await readFile(canonicalPath, 'utf8'));
    const actualHash = sha256(license);
    if (actualHash !== licensingPolicy.canonicalLicense.normalizedSha256) {
      errors.push(createError(
        'LICENSE_NONCANONICAL',
        licensingPolicy.canonicalLicense.path,
        `expected SHA-256 ${licensingPolicy.canonicalLicense.normalizedSha256}, got ${actualHash}`,
      ));
    }
  }

  for (const requiredFile of licensingPolicy.requiredFiles) {
    if (!(await pathExists(path.join(repoRoot, requiredFile)))) {
      errors.push(createError('COMPLIANCE_FILE_MISSING', requiredFile, 'required compliance file is missing'));
    }
  }

  for (const licenseRecord of licensingPolicy.preservedThirdPartyLicenses ?? []) {
    const contextPath = licenseRecord.path ?? 'compliance/licensing-policy.json';
    const absolutePath = path.resolve(repoRoot, contextPath);
    const relativePath = path.relative(repoRoot, absolutePath);
    if (relativePath.startsWith('..') || path.isAbsolute(relativePath)) {
      errors.push(createError('THIRD_PARTY_LICENSE_PATH', contextPath, 'license path escapes the repository'));
      continue;
    }
    if (!isAbsoluteHttpsUrl(licenseRecord.source)) {
      errors.push(createError('THIRD_PARTY_LICENSE_SOURCE', contextPath, 'source must be an absolute HTTPS URL'));
    }
    if (!(await pathExists(absolutePath))) {
      errors.push(createError('THIRD_PARTY_LICENSE_MISSING', contextPath, 'preserved license file is missing'));
      continue;
    }
    const actualDigest = normalizedLicenseSha256(await readFile(absolutePath));
    if (actualDigest !== licenseRecord.sha256) {
      errors.push(createError(
        'THIRD_PARTY_LICENSE_CHANGED',
        contextPath,
        `expected SHA-256 ${licenseRecord.sha256}, got ${actualDigest}`,
      ));
    }
    for (const appliedPath of licenseRecord.appliesTo ?? []) {
      if (!(await pathExists(path.join(repoRoot, appliedPath)))) {
        errors.push(createError('THIRD_PARTY_COMPONENT_MISSING', appliedPath, 'licensed component is missing'));
      }
    }
  }

  const versionPath = path.join(repoRoot, 'VERSION');
  const currentVersion = await pathExists(versionPath)
    ? (await readFile(versionPath, 'utf8')).trim()
    : undefined;
  const parsedCurrentVersion = parseSemanticVersion(currentVersion);
  const parsedEffectiveVersion = parseSemanticVersion(licensingPolicy.decision.effectiveVersion);
  if (!parsedCurrentVersion
    || !parsedEffectiveVersion
    || compareSemanticVersions(parsedCurrentVersion, parsedEffectiveVersion) < 0) {
    errors.push(createError(
      'LICENSE_EFFECTIVE_VERSION',
      'VERSION',
      `VERSION must be at or after licensing boundary ${licensingPolicy.decision.effectiveVersion}`,
    ));
  }

  const buildPropsPath = path.join(repoRoot, 'Directory.Build.props');
  if (parsedCurrentVersion && await pathExists(buildPropsPath)) {
    const buildProps = await readFile(buildPropsPath, 'utf8');
    const packageVersion = currentVersion.replace(/^v/, '');
    const versionPrefix = /<VersionPrefix\b[^>]*>([^<]+)<\/VersionPrefix>/.exec(buildProps)?.[1];
    const derivesVersionFromRepositoryFile = [
      "<RepoVersionRaw>$([System.IO.File]::ReadAllText('$(MSBuildThisFileDirectory)VERSION').Trim())</RepoVersionRaw>",
      "<RepoVersion>$([System.Text.RegularExpressions.Regex]::Replace('$(RepoVersionRaw)', '^v', ''))</RepoVersion>",
      '<Version>$(RepoVersion)</Version>',
    ].every((metadata) => buildProps.includes(metadata));
    if (versionPrefix !== packageVersion && !derivesVersionFromRepositoryFile) {
      errors.push(createError(
        'VERSION_METADATA_MISMATCH',
        'Directory.Build.props',
        `MSBuild version metadata must match or derive from VERSION (${packageVersion})`,
      ));
    }
  }

  for (const manifestPath of licensingPolicy.packageManifests) {
    const absolutePath = path.join(repoRoot, manifestPath);
    if (!(await pathExists(absolutePath))) {
      errors.push(createError('PACKAGE_MANIFEST_MISSING', manifestPath, 'first-party package manifest is missing'));
      continue;
    }

    const manifest = await readJson(absolutePath);
    if (manifest.license !== licensingPolicy.licenseExpression) {
      errors.push(createError(
        'PACKAGE_LICENSE_CONFLICT',
        manifestPath,
        `expected ${licensingPolicy.licenseExpression}, got ${manifest.license ?? 'missing'}`,
      ));
    }

    const repositoryUrl = typeof manifest.repository === 'string'
      ? manifest.repository
      : manifest.repository?.url;
    if (!repositoryUrl?.includes('github.com/OlyForge3D/PrintFarmer')) {
      errors.push(createError('PACKAGE_REPOSITORY_MISSING', manifestPath, 'repository URL must identify OlyForge3D/PrintFarmer'));
    }
  }

  for (const assertion of licensingPolicy.metadataAssertions) {
    const absolutePath = path.join(repoRoot, assertion.path);
    if (!(await pathExists(absolutePath))) {
      errors.push(createError('METADATA_FILE_MISSING', assertion.path, 'metadata assertion file is missing'));
      continue;
    }

    const content = await readFile(absolutePath, 'utf8');
    for (const requiredText of assertion.contains ?? []) {
      if (!content.includes(requiredText)) {
        errors.push(createError(
          'METADATA_ASSERTION',
          assertion.path,
          `required metadata is missing: ${requiredText}`,
        ));
      }
    }
    for (const prohibitedText of assertion.notContains ?? []) {
      if (content.includes(prohibitedText)) {
        errors.push(createError(
          'METADATA_REJECTION',
          assertion.path,
          `prohibited metadata is present: ${prohibitedText}`,
        ));
      }
    }
    let previousIndex = -1;
    for (const orderedText of assertion.orderedContains ?? []) {
      const currentIndex = content.indexOf(orderedText, previousIndex + 1);
      if (currentIndex < 0) {
        errors.push(createError(
          'METADATA_ORDER',
          assertion.path,
          `required metadata is missing or out of order: ${orderedText}`,
        ));
        break;
      }
      previousIndex = currentIndex;
    }
  }

  for (const scanPath of licensingPolicy.forbiddenScanPaths) {
    const absolutePath = path.join(repoRoot, scanPath);
    if (!(await pathExists(absolutePath))) {
      continue;
    }

    const content = (await readFile(absolutePath, 'utf8')).toLowerCase();
    for (const declaration of licensingPolicy.forbiddenFirstPartyDeclarations) {
      if (content.includes(declaration.toLowerCase())) {
        errors.push(createError(
          'LICENSE_CONFLICT',
          scanPath,
          `conflicting first-party declaration found: ${declaration}`,
        ));
      }
    }
  }

  for (const exception of licensingPolicy.exceptions) {
    errors.push(...validateExceptionRecord(
      exception,
      `compliance/licensing-policy.json:exceptions:${exception.id ?? '<missing>'}`,
    ));
  }

  return errors;
}

function getPackagePurl(packageRecord) {
  return (packageRecord.externalRefs ?? [])
    .find((reference) =>
      reference.referenceType === 'purl'
      || reference.referenceLocator?.startsWith('pkg:'))
    ?.referenceLocator;
}

function parsePurl(purl) {
  if (!purl?.startsWith('pkg:')) {
    return undefined;
  }

  try {
    const packageAndQuery = purl.slice('pkg:'.length).split('?', 1)[0];
    const typeSeparator = packageAndQuery.indexOf('/');
    if (typeSeparator < 1) {
      return undefined;
    }

    const type = packageAndQuery.slice(0, typeSeparator).toLowerCase();
    const identity = packageAndQuery.slice(typeSeparator + 1);
    const versionSeparator = identity.lastIndexOf('@');
    const packagePath = versionSeparator < 0 ? identity : identity.slice(0, versionSeparator);
    const pathSegments = packagePath.split('/');
    return {
      name: decodeURIComponent(pathSegments.at(-1)),
      namespace: pathSegments.slice(0, -1).map(decodeURIComponent).join('/'),
      type,
      version: versionSeparator < 0
        ? undefined
        : decodeURIComponent(identity.slice(versionSeparator + 1)),
    };
  } catch {
    return undefined;
  }
}

function setPackagePurl(packageRecord, purl) {
  packageRecord.externalRefs ??= [];
  const existingReference = packageRecord.externalRefs.find((reference) =>
    reference.referenceType === 'purl'
    || reference.referenceLocator?.startsWith('pkg:'));
  if (existingReference) {
    existingReference.referenceCategory = 'PACKAGE-MANAGER';
    existingReference.referenceType = 'purl';
    existingReference.referenceLocator = purl;
  } else {
    packageRecord.externalRefs.push({
      referenceCategory: 'PACKAGE-MANAGER',
      referenceType: 'purl',
      referenceLocator: purl,
    });
  }
}

function appendSourceInfo(packageRecord, statement) {
  const existing = packageRecord.sourceInfo;
  packageRecord.sourceInfo = existing && existing !== 'NOASSERTION'
    ? `${existing}; ${statement}`
    : statement;
}

function addInventoryLicenseText(sbom, inventoryPackage) {
  const errors = [];
  for (const match of inventoryPackage.licenseExpression.matchAll(/LicenseRef-[A-Za-z0-9.-]+/g)) {
    sbom.hasExtractedLicensingInfos ??= [];
    const existing = sbom.hasExtractedLicensingInfos
      .find((license) => license.licenseId === match[0]);
    if (existing) {
      if (existing.extractedText !== inventoryPackage.licenseText) {
        errors.push(createError(
          'SBOM_LICENSE_TEXT_CONFLICT',
          inventoryPackage.purl,
          `custom license ${match[0]} has conflicting extracted text`,
        ));
      }
      continue;
    }

    if (!inventoryPackage.licenseText) {
      errors.push(createError(
        'SBOM_LICENSE_TEXT_MISSING',
        inventoryPackage.purl,
        `custom license ${match[0]} requires extracted text`,
      ));
      continue;
    }

    sbom.hasExtractedLicensingInfos.push({
      extractedText: inventoryPackage.licenseText,
      licenseId: match[0],
      name: `${inventoryPackage.name} reviewed license terms`,
    });
  }

  return errors;
}

function applyInventoryPackage(sbom, packageRecord, inventoryPackage) {
  packageRecord.versionInfo = inventoryPackage.version;
  packageRecord.licenseDeclared = inventoryPackage.licenseExpression;
  packageRecord.downloadLocation =
    `https://www.nuget.org/packages/${encodeURIComponent(inventoryPackage.name)}/${encodeURIComponent(inventoryPackage.version)}`;
  setPackagePurl(packageRecord, inventoryPackage.purl);
  appendSourceInfo(
    packageRecord,
    `license and package identity resolved from ${inventoryPackage.name}@${inventoryPackage.version}`,
  );
  return addInventoryLicenseText(sbom, inventoryPackage);
}

function copyPackageEvidence(target, source, statement, copyPurl = true) {
  target.licenseDeclared = source.licenseDeclared;
  target.licenseConcluded = source.licenseConcluded;
  target.supplier = source.supplier;
  target.downloadLocation = source.downloadLocation;
  const sourcePurl = getPackagePurl(source);
  if (sourcePurl && copyPurl) {
    setPackagePurl(target, sourcePurl);
  }

  if (source.sourceInfo && source.sourceInfo !== 'NOASSERTION') {
    appendSourceInfo(target, source.sourceInfo);
  }
  appendSourceInfo(target, statement);
}

function findInventoryPackage(packagesByName, name, version) {
  const candidates = packagesByName.get(name.toLowerCase()) ?? [];
  return candidates.find((candidate) => candidate.version === version);
}

function sourceInfoApplicationPaths(packageRecord) {
  const marker = 'paths:';
  const markerIndex = packageRecord.sourceInfo?.indexOf(marker) ?? -1;
  if (markerIndex < 0) {
    return [];
  }

  return packageRecord.sourceInfo
    .slice(markerIndex + marker.length)
    .split(',')
    .map((entry) => entry.trim())
    .filter((entry) => entry.startsWith('/app/'))
    .map((entry) => normalizeRelativePath(entry.slice('/app/'.length)).toLowerCase());
}

export function enrichSbomDocument(sbom, inventory, dependencyPolicy, options) {
  const errors = [];
  if (inventory.schemaVersion !== 1) {
    return [createError('SBOM_INVENTORY_SCHEMA', options.inventoryPath, 'license inventory schemaVersion must be 1')];
  }

  if (inventory.revision !== options.revision || inventory.version !== options.version) {
    return [createError(
      'SBOM_INVENTORY_REVISION',
      options.inventoryPath,
      'license inventory version and revision must match the image build',
    )];
  }

  const projects = new Set(inventory.projects.map((project) => project.name.toLowerCase()));
  const packagesByName = new Map();
  const packagesByFile = new Map();
  const packagesByFileName = new Map();
  for (const packageRecord of inventory.packages) {
    const nameKey = packageRecord.name.toLowerCase();
    const nameRecords = packagesByName.get(nameKey) ?? [];
    nameRecords.push(packageRecord);
    packagesByName.set(nameKey, nameRecords);

    for (const file of packageRecord.files) {
      const fileKey = normalizeRelativePath(file).toLowerCase();
      const fileRecords = packagesByFile.get(fileKey) ?? [];
      fileRecords.push(packageRecord);
      packagesByFile.set(fileKey, fileRecords);

      const fileNameKey = path.posix.basename(fileKey);
      const fileNameRecords = packagesByFileName.get(fileNameKey) ?? [];
      fileNameRecords.push(packageRecord);
      packagesByFileName.set(fileNameKey, fileNameRecords);
    }
  }

  if (options.includeNpm) {
    const rootPackage = (sbom.packages ?? [])
      .find((packageRecord) => packageRecord.SPDXID?.startsWith('SPDXRef-DocumentRoot-'));
    if (!rootPackage) {
      errors.push(createError('SBOM_NPM_ROOT', options.inventoryPath, 'image root package is required for npm relationships'));
    }

    if (!Array.isArray(inventory.npmPackages) || inventory.npmPackages.length === 0) {
      errors.push(createError('SBOM_NPM_INVENTORY', options.inventoryPath, 'production npm inventory is empty'));
    } else if (rootPackage) {
      sbom.relationships ??= [];
      const existingPurls = new Set((sbom.packages ?? []).map(getPackagePurl).filter(Boolean));
      for (const npmPackage of inventory.npmPackages) {
        if (existingPurls.has(npmPackage.purl)) {
          continue;
        }

        const spdxId = `SPDXRef-Npm-${sha256(npmPackage.purl).slice(0, 24)}`;
        sbom.packages.push({
          SPDXID: spdxId,
          downloadLocation: npmPackage.resolved
            ?? `https://www.npmjs.com/package/${encodeURIComponent(npmPackage.name)}/v/${encodeURIComponent(npmPackage.version)}`,
          externalRefs: [{
            referenceCategory: 'PACKAGE-MANAGER',
            referenceLocator: npmPackage.purl,
            referenceType: 'purl',
          }],
          filesAnalyzed: false,
          licenseDeclared: npmPackage.licenseExpression,
          name: npmPackage.name,
          sourceInfo: 'production frontend dependency resolved from src/Web/ReactApp/package-lock.json',
          versionInfo: npmPackage.version,
        });
        sbom.relationships.push({
          relatedSpdxElement: spdxId,
          relationshipType: 'CONTAINS',
          spdxElementId: rootPackage.SPDXID,
        });
      }
    }
  }

  const distroPackages = new Map();
  for (const packageRecord of sbom.packages ?? []) {
    const parsedPurl = parsePurl(getPackagePurl(packageRecord));
    if (parsedPurl && ['apk', 'deb'].includes(parsedPurl.type)) {
      distroPackages.set(
        `${parsedPurl.name.toLowerCase()}@${packageRecord.versionInfo}`.toLowerCase(),
        packageRecord,
      );
    }
  }

  for (const packageRecord of sbom.packages ?? []) {
    const parsedPurl = parsePurl(getPackagePurl(packageRecord));
    if (!parsedPurl) {
      continue;
    }

    const evidence = (dependencyPolicy.sbom?.packageEvidence ?? []).find((record) =>
      record.purlType === parsedPurl.type
      && new RegExp(record.namePattern, 'i').test(parsedPurl.name)
      && (!record.namespacePattern
        || new RegExp(record.namespacePattern, 'i').test(parsedPurl.namespace))
      && new RegExp(record.versionPattern).test(packageRecord.versionInfo ?? ''));
    if (evidence) {
      packageRecord.licenseDeclared = evidence.approvedExpression;
      packageRecord.supplier = evidence.supplier;
      packageRecord.downloadLocation = evidence.sourceUrl;
      appendSourceInfo(packageRecord, `license resolved from reviewed evidence: ${evidence.evidence}`);
    }
  }

  for (const packageRecord of distroPackages.values()) {
    const parsedPurl = parsePurl(getPackagePurl(packageRecord));
    const alias = (dependencyPolicy.sbom?.distroAliases ?? [])
      .find((record) => record.name.toLowerCase() === parsedPurl.name.toLowerCase());
    if (!alias) {
      continue;
    }

    const source = distroPackages.get(
      `${alias.licenseSourcePackage.toLowerCase()}@${packageRecord.versionInfo}`.toLowerCase(),
    );
    const sourceLicense = [source?.licenseDeclared, source?.licenseConcluded]
      .find((license) => license && !dependencyPolicy.deniedValues.includes(license));
    if (source && sourceLicense) {
      copyPackageEvidence(
        packageRecord,
        source,
        `license copied from same-version distro package ${alias.licenseSourcePackage}`,
        false,
      );
    }
  }

  for (const packageRecord of sbom.packages ?? []) {
    const packageName = packageRecord.name ?? '';
    if (packageRecord.SPDXID?.startsWith('SPDXRef-DocumentRoot-')) {
      packageRecord.versionInfo = options.version;
      packageRecord.licenseDeclared = options.licenseExpression;
      packageRecord.supplier = 'Organization: OlyForge3D';
      packageRecord.downloadLocation = `${options.repositoryUrl}/tree/${options.revision}`;
      setPackagePurl(
        packageRecord,
        `pkg:generic/olyforge3d/printfarmer-image@${encodeURIComponent(options.version)}`,
      );
      appendSourceInfo(packageRecord, `image built from ${options.repositoryUrl}/tree/${options.revision}`);
      continue;
    }

    if (projects.has(packageName.toLowerCase())) {
      packageRecord.versionInfo = options.version;
      packageRecord.licenseDeclared = options.licenseExpression;
      packageRecord.supplier = 'Organization: OlyForge3D';
      packageRecord.downloadLocation = `${options.repositoryUrl}/tree/${options.revision}`;
      setPackagePurl(
        packageRecord,
        `pkg:generic/olyforge3d/${encodeURIComponent(packageName)}@${encodeURIComponent(options.version)}`,
      );
      appendSourceInfo(packageRecord, `built from ${options.repositoryUrl}/tree/${options.revision}`);
      continue;
    }

    const parsedPurl = parsePurl(getPackagePurl(packageRecord));
    if (parsedPurl?.type === 'nuget') {
      const inventoryPackage = findInventoryPackage(
        packagesByName,
        parsedPurl.name,
        parsedPurl.version ?? packageRecord.versionInfo,
      );
      if (inventoryPackage) {
        errors.push(...applyInventoryPackage(sbom, packageRecord, inventoryPackage));
        continue;
      }

      const runtimeEvidence = (dependencyPolicy.sbom?.runtimePackageEvidence ?? [])
        .find((record) => new RegExp(record.namePattern, 'i').test(parsedPurl.name));
      if (runtimeEvidence) {
        packageRecord.licenseDeclared = runtimeEvidence.approvedExpression;
        packageRecord.supplier = runtimeEvidence.supplier;
        packageRecord.downloadLocation = runtimeEvidence.sourceUrl;
        appendSourceInfo(packageRecord, `license resolved from reviewed runtime evidence: ${runtimeEvidence.evidence}`);
        continue;
      }
    }

    const nativeCandidates = new Map();
    for (const applicationPath of sourceInfoApplicationPaths(packageRecord)) {
      const pathCandidates = [
        ...(packagesByFile.get(applicationPath) ?? []),
        ...(packagesByFileName.get(path.posix.basename(applicationPath)) ?? []),
      ];
      for (const inventoryPackage of pathCandidates) {
        if (parsedPurl?.type === 'nuget'
          && inventoryPackage.name.toLowerCase() !== parsedPurl.name.toLowerCase()) {
          continue;
        }

        nativeCandidates.set(
          `${inventoryPackage.name.toLowerCase()}@${inventoryPackage.version}`,
          inventoryPackage,
        );
      }
    }
    if (nativeCandidates.size === 1) {
      errors.push(...applyInventoryPackage(sbom, packageRecord, [...nativeCandidates.values()][0]));
    }
  }

  for (const packageRecord of sbom.packages ?? []) {
    const parsedPurl = parsePurl(getPackagePurl(packageRecord));
    if (parsedPurl?.type !== 'generic') {
      continue;
    }

    const distroCandidates = [...distroPackages.values()].filter((candidate) => {
      const candidatePurl = parsePurl(getPackagePurl(candidate));
      return candidatePurl?.name.toLowerCase() === parsedPurl.name.toLowerCase()
        && (candidate.versionInfo === packageRecord.versionInfo
          || candidate.versionInfo?.startsWith(`${packageRecord.versionInfo}-`));
    });
    const distroPackage = distroCandidates.length === 1 ? distroCandidates[0] : undefined;
    const distroLicense = [distroPackage?.licenseDeclared, distroPackage?.licenseConcluded]
      .find((license) => license && !dependencyPolicy.deniedValues.includes(license));
    if (distroPackage && distroLicense) {
      copyPackageEvidence(
        packageRecord,
        distroPackage,
        `binary record reconciled to distro package ${parsedPurl.name}@${packageRecord.versionInfo}`,
      );
    }
  }

  return errors;
}

function validateSbomLicense(
  sbom,
  dependencyPolicy,
  packageRecord,
  packageName,
  packageVersion,
  observedLicense,
  purl,
  contextPath,
) {
  const ecosystemReview = (dependencyPolicy.sbom?.reviewedEcosystems ?? [])
    .find((record) => record.purlPrefixes.some((prefix) =>
      purl?.toLowerCase().startsWith(prefix.toLowerCase())));
  const errors = !ecosystemReview || dependencyPolicy.deniedValues.includes(observedLicense)
    ? validateObservedLicense(
      dependencyPolicy,
      'sbom',
      packageName,
      packageVersion,
      observedLicense,
      contextPath,
    )
    : validateException(ecosystemReview, contextPath);
  if (ecosystemReview) {
    for (const deniedPattern of dependencyPolicy.sbom.deniedLicensePatterns ?? []) {
      if (new RegExp(deniedPattern, 'i').test(observedLicense)) {
        errors.push(createError(
          'LICENSE_UNREVIEWED',
          contextPath,
          `reviewed ecosystem license matches denied pattern ${deniedPattern}`,
        ));
      }
    }
  }

  const extractedLicenses = new Map(
    (sbom.hasExtractedLicensingInfos ?? [])
      .map((license) => [license.licenseId, license]),
  );
  for (const match of observedLicense.matchAll(/LicenseRef-[A-Za-z0-9.-]+/g)) {
    const licenseInfo = extractedLicenses.get(match[0]);
    const hasExtractedText = licenseInfo?.extractedText
      && !dependencyPolicy.deniedValues.includes(licenseInfo.extractedText);
    const hasInImageEvidence = ecosystemReview
      && licenseInfo?.name
      && !dependencyPolicy.deniedValues.includes(licenseInfo.name)
      && /\/usr\/share\/(?:doc|licenses)\//.test(packageRecord.sourceInfo ?? '');
    if (!hasExtractedText && !hasInImageEvidence) {
      errors.push(createError(
        'LICENSE_UNREVIEWED',
        contextPath,
        ecosystemReview
          ? `custom license ${match[0]} requires extracted text or an in-image copyright path`
          : `custom license ${match[0]} requires extracted text`,
      ));
    }

    for (const deniedPattern of dependencyPolicy.sbom.deniedLicensePatterns ?? []) {
      if (new RegExp(deniedPattern, 'i').test(licenseInfo?.name ?? '')) {
        errors.push(createError(
          'LICENSE_UNREVIEWED',
          contextPath,
          `custom license ${match[0]} name matches denied pattern ${deniedPattern}`,
        ));
      }
    }
  }

  return errors;
}

export function validateSbomDocument(sbom, sbomPath, dependencyPolicy) {
  const errors = [];
  if (!/^SPDX-2\./.test(sbom.spdxVersion ?? '')) {
    errors.push(createError('SBOM_VERSION', sbomPath, 'spdxVersion must identify SPDX 2.x JSON'));
  }

  if (sbom.dataLicense !== 'CC0-1.0') {
    errors.push(createError('SBOM_DATA_LICENSE', sbomPath, 'dataLicense must be CC0-1.0'));
  }

  if (!/^https:\/\//.test(sbom.documentNamespace ?? '')) {
    errors.push(createError('SBOM_NAMESPACE', sbomPath, 'documentNamespace must be a durable HTTPS identifier'));
  }

  if (!sbom.creationInfo?.created || !Array.isArray(sbom.creationInfo?.creators)
    || sbom.creationInfo.creators.length === 0) {
    errors.push(createError('SBOM_CREATION_INFO', sbomPath, 'creationInfo must include timestamp and creator'));
  }

  if (!Array.isArray(sbom.packages) || sbom.packages.length === 0) {
    errors.push(createError('SBOM_PACKAGES', sbomPath, 'SBOM must contain at least one package'));
    return errors;
  }

  for (const packageRecord of sbom.packages) {
    const packageName = packageRecord.name ?? '<missing>';
    const packageVersion = packageRecord.versionInfo ?? 'unknown';
    const contextPath = `${sbomPath}:${packageName}@${packageVersion}`;
    if (!packageRecord.SPDXID || packageName === '<missing>' || packageVersion === 'unknown') {
      errors.push(createError('SBOM_COMPONENT_IDENTITY', contextPath, 'component requires SPDXID, name, and versionInfo'));
    }

    const externalReferences = packageRecord.externalRefs ?? [];
    const hasPurl = externalReferences.some((reference) =>
      reference.referenceType === 'purl' || reference.referenceLocator?.startsWith('pkg:'));
    const hasDownloadSource = packageRecord.downloadLocation
      && packageRecord.downloadLocation !== 'NOASSERTION'
      && packageRecord.downloadLocation !== 'NONE';
    const hasSupplier = packageRecord.supplier
      && packageRecord.supplier !== 'NOASSERTION'
      && packageRecord.supplier !== 'NONE';
    if (!hasPurl && !hasDownloadSource && !hasSupplier) {
      errors.push(createError(
        'SBOM_COMPONENT_SOURCE',
        contextPath,
        'component requires a supplier, source location, or package URL',
      ));
    }

    const observedLicense = [packageRecord.licenseDeclared, packageRecord.licenseConcluded]
      .find((license) => license && !dependencyPolicy.deniedValues.includes(license)) ?? 'NOASSERTION';
    errors.push(...validateSbomLicense(
      sbom,
      dependencyPolicy,
      packageRecord,
      packageName,
      packageVersion,
      observedLicense,
      getPackagePurl(packageRecord),
      contextPath,
    ));
  }

  if (!Array.isArray(sbom.relationships) || sbom.relationships.length === 0) {
    errors.push(createError('SBOM_RELATIONSHIPS', sbomPath, 'SBOM must describe component relationships'));
  }

  return errors;
}

function isVariableCredentialTemplate(value) {
  return /\$\{[^}]+\}|\$[A-Za-z_][A-Za-z0-9_]*/.test(value);
}

function scanPathMatchesException(scanPath, exceptionPath) {
  const normalizedExceptionPath = normalizeRelativePath(exceptionPath);
  return scanPath === normalizedExceptionPath
    || scanPath.endsWith(`/${normalizedExceptionPath}`);
}

export async function scanPublicationFiles(
  repoRoot,
  relativePaths,
  secretPatterns,
  secretPatternExceptions = [],
) {
  const errors = [];
  const patterns = secretPatterns.map((source) => ({
    expression: new RegExp(source, 'gi'),
    source,
  }));
  const validExceptions = [];
  const usedExceptions = new Set();

  for (const [index, exception] of secretPatternExceptions.entries()) {
    const exceptionPath = normalizeRelativePath(exception.path ?? '');
    const isValid = isSafeRepositoryPath(exceptionPath)
      && secretPatterns.includes(exception.pattern)
      && /^[a-f0-9]{64}$/.test(exception.matchSha256 ?? '')
      && typeof exception.reason === 'string'
      && exception.reason.trim().length > 0;
    if (!isValid) {
      errors.push(createError(
        'PUBLICATION_SECRET_EXCEPTION_INVALID',
        `secretPatternExceptions[${index}]`,
        'exception requires a safe path, known pattern, SHA-256 match hash, and rationale',
      ));
      continue;
    }
    validExceptions.push({ ...exception, index, path: exceptionPath });
  }

  const scannedPaths = [];

  for (const relativePath of relativePaths) {
    const absolutePath = path.resolve(repoRoot, relativePath);
    const relativeToRoot = normalizeRelativePath(path.relative(repoRoot, absolutePath));
    if (!isSafeRepositoryPath(relativeToRoot)) {
      errors.push(createError('PUBLICATION_FILE_MISSING', relativePath, 'publication file is missing or outside the repository'));
      continue;
    }

    // Opens the file once and checks size / reads content from that same
    // file handle (fstat + read on one fd), instead of a path-based stat()
    // followed by a separate path-based readFile(). Operating on one open
    // descriptor for both the size check and the read closes the TOCTOU
    // window entirely, because both operations observe the same underlying
    // file regardless of what happens to the path afterward.
    let handle;
    try {
      handle = await open(absolutePath, 'r');
    } catch {
      errors.push(createError('PUBLICATION_FILE_MISSING', relativePath, 'publication file is missing or outside the repository'));
      continue;
    }
    let content;
    try {
      const fileStat = await handle.stat();
      if (fileStat.size > 20_000_000) {
        errors.push(createError('PUBLICATION_FILE_SIZE', relativePath, 'publication scan only accepts files up to 20 MB'));
        continue;
      }
      content = await handle.readFile('utf8');
    } finally {
      await handle.close();
    }
    scannedPaths.push(relativeToRoot);
    for (const pattern of patterns) {
      pattern.expression.lastIndex = 0;
      for (const match of content.matchAll(pattern.expression)) {
        if (isVariableCredentialTemplate(match[0])) {
          continue;
        }

        const matchSha256 = createHash('sha256').update(match[0]).digest('hex');
        const exception = validExceptions.find((candidate) =>
          scanPathMatchesException(relativeToRoot, candidate.path)
          && candidate.pattern === pattern.source
          && candidate.matchSha256 === matchSha256);
        if (exception) {
          usedExceptions.add(exception.index);
          continue;
        }

        errors.push(createError(
          'PUBLICATION_SECRET',
          relativePath,
          `content matches prohibited secret pattern ${pattern.source}`,
        ));
        break;
      }
    }
  }

  for (const exception of validExceptions) {
    if (!usedExceptions.has(exception.index)) {
      const pathWasScanned = scannedPaths.some((scanPath) =>
        scanPathMatchesException(scanPath, exception.path));
      errors.push(createError(
        'PUBLICATION_SECRET_EXCEPTION_STALE',
        exception.path,
        pathWasScanned
          ? 'hash-bound exception no longer matches the reviewed content'
          : 'hash-bound exception path was not scanned',
      ));
    }
  }

  return errors;
}

export async function validateRepository(repoRoot, options = {}) {
  const licensingPolicyPath = path.join(repoRoot, 'compliance', 'licensing-policy.json');
  const dependencyPolicyPath = path.join(repoRoot, 'compliance', 'dependency-license-policy.json');
  const errors = [];

  for (const requiredPolicyPath of [licensingPolicyPath, dependencyPolicyPath]) {
    if (!(await pathExists(requiredPolicyPath))) {
      errors.push(createError(
        'POLICY_MISSING',
        path.relative(repoRoot, requiredPolicyPath),
        'required policy file is missing',
      ));
    }
  }

  if (errors.length > 0) {
    return errors;
  }

  const licensingPolicy = await readJson(licensingPolicyPath);
  const dependencyPolicy = await readJson(dependencyPolicyPath);

  errors.push(...await validateLicenseMetadata(repoRoot, licensingPolicy));

  if (options.includeDependencies !== false) {
    errors.push(...await validateDependencyLicenses(repoRoot, dependencyPolicy));
  }

  for (const sbomPath of options.sbomPaths ?? []) {
    const absoluteSbomPath = path.resolve(repoRoot, sbomPath);
    if (!(await pathExists(absoluteSbomPath))) {
      errors.push(createError('SBOM_MISSING', sbomPath, 'SBOM file does not exist'));
      continue;
    }

    errors.push(...validateSbomDocument(
      await readJson(absoluteSbomPath),
      normalizeRelativePath(path.relative(repoRoot, absoluteSbomPath)),
      dependencyPolicy,
    ));
  }

  if ((options.publicationPaths ?? []).length > 0) {
    errors.push(...await scanPublicationFiles(
      repoRoot,
      options.publicationPaths,
      licensingPolicy.publication.secretPatterns,
    ));
  }

  return errors;
}
