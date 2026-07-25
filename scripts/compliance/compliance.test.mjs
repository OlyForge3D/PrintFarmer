import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import {
  copyFile,
  mkdtemp,
  mkdir,
  rm,
  writeFile,
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import {
  createNpmLicenseInventory,
  createNugetLicenseInventory,
  enrichSbomDocument,
  scanPublicationFiles,
  findNugetAssetsFiles,
  sha256,
  validateLicenseMetadata,
  validateDependencyLicenses,
  validateProvenanceManifest,
  validateSbomDocument,
} from './compliance-lib.mjs';
import { scanSourceArchive } from './create-source-bundle.mjs';

const validCommit = '0123456789abcdef0123456789abcdef01234567';
const validBlob = 'sha1:89abcdef0123456789abcdef0123456789abcdef';
const execFileAsync = promisify(execFile);
const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

async function createFixture() {
  const root = await mkdtemp(path.join(tmpdir(), 'printfarmer-compliance-'));
  const destinationPath = 'src/Farm/Calibration/Derived/FlowMath.cs';
  const testPath = 'src/tests/FlowMathTests.cs';
  await mkdir(path.join(root, path.dirname(destinationPath)), { recursive: true });
  await mkdir(path.join(root, path.dirname(testPath)), { recursive: true });
  await mkdir(path.join(root, 'compliance'), { recursive: true });
  await copyFile(
    path.join(repositoryRoot, 'compliance', 'calibration-provenance.schema.json'),
    path.join(root, 'compliance', 'calibration-provenance.schema.json'),
  );

  const content = [
    '// SPDX-License-Identifier: AGPL-3.0-only',
    '// PrintFarmer-Provenance-ID: orca-flow-math',
    '// Copyright upstream contributors',
    'internal static class FlowMath {}',
    '',
  ].join('\n');
  await writeFile(path.join(root, destinationPath), content);
  await writeFile(path.join(root, testPath), 'internal sealed class FlowMathTests {}\n');

  const manifest = {
    schemaVersion: 1,
    policyVersion: '1.0.0',
    lastReviewed: '2026-07-24',
    allowedSourceLicenseExpressions: ['AGPL-3.0-only', 'MIT'],
    governedPathPatterns: ['src/**/Calibration/Derived/**'],
    approvedSources: [{
      id: 'orca-v1',
      repositoryUrl: 'https://github.com/OrcaSlicer/OrcaSlicer',
      tag: 'v1.0.0',
      commitSha: validCommit,
      licenseExpression: 'AGPL-3.0-only',
      licenseEvidence: {
        path: 'LICENSE.txt',
        blobDigest: validBlob,
      },
      review: {
        reviewer: 'Maintainer',
        reviewDate: '2026-07-24',
        decision: 'approved',
      },
    }],
    entries: [{
      id: 'orca-flow-math',
      sourceId: 'orca-v1',
      sourcePath: 'src/flow_math.cpp',
      sourceBlobDigest: validBlob,
      destinationPath,
      destinationSha256: sha256(Buffer.from(content)),
      derivation: 'adapted',
      upstreamCopyright: 'Copyright upstream contributors',
      upstreamSpdx: 'AGPL-3.0-only',
      preservedNotices: ['Copyright upstream contributors'],
      modificationSummary: 'Adapted types for PrintFarmer.',
      architecturalChanges: 'Removed UI and filesystem dependencies.',
      review: {
        reviewer: 'Maintainer',
        reviewDate: '2026-07-24',
        decision: 'approved',
      },
      firstPrintFarmerVersion: 'v0.2.3',
      tests: [testPath],
      replacementHistory: [],
    }],
    referenceRecords: [],
    exceptions: [],
  };

  return {
    content,
    destinationPath,
    manifest,
    root,
    testPath,
  };
}

async function withFixture(callback) {
  const fixture = await createFixture();
  try {
    await callback(fixture);
  } finally {
    await rm(fixture.root, { force: true, recursive: true });
  }
}

function hasCode(errors, code) {
  return errors.some((error) => error.code === code);
}

async function createNugetLicenseFixture(licenseMetadata, licenseContent = undefined) {
  const root = await mkdtemp(path.join(tmpdir(), 'printfarmer-nuget-license-'));
  const packageName = 'Example.Package';
  const version = '1.0.0';
  const packageCache = path.join(root, 'packages');
  const packagePath = path.join(packageCache, packageName.toLowerCase(), version);
  const assetsPath = path.join(root, 'src', 'app', 'obj', 'project.assets.json');
  await mkdir(packagePath, { recursive: true });
  await mkdir(path.dirname(assetsPath), { recursive: true });
  await writeFile(
    path.join(packagePath, `${packageName.toLowerCase()}.nuspec`),
    `<package><metadata><id>${packageName}</id><version>${version}</version>${licenseMetadata}</metadata></package>`,
  );
  if (licenseContent !== undefined) {
    await writeFile(path.join(packagePath, 'LICENSE.txt'), licenseContent);
  }

  await writeFile(assetsPath, JSON.stringify({
    packageFolders: {
      [`${packageCache}${path.sep}`]: {},
    },
    libraries: {
      [`${packageName}/${version}`]: {
        type: 'package',
      },
    },
    project: {
      restore: {
        projectPath: path.join(root, 'src', 'app', 'app.csproj'),
      },
    },
  }));

  return {
    packageName,
    packagePath,
    root,
    version,
  };
}

function nugetPolicy(reviewedEvidence) {
  return {
    allowedExpressions: ['MIT'],
    deniedValues: ['', 'MISSING', 'NONE', 'NOASSERTION', 'UNKNOWN', 'UNLICENSED'],
    npmLockFiles: [],
    nuget: {
      assetsRoot: 'src',
      excludedProjectPathSegments: [],
    },
    reviewedEvidence,
    reviewedExceptions: [],
  };
}

function sbomPackage(SPDXID, name, versionInfo, purl, overrides = {}) {
  return {
    SPDXID,
    name,
    versionInfo,
    downloadLocation: 'NOASSERTION',
    licenseDeclared: 'NOASSERTION',
    externalRefs: purl ? [{
      referenceCategory: 'PACKAGE-MANAGER',
      referenceType: 'purl',
      referenceLocator: purl,
    }] : [],
    ...overrides,
  };
}

function sbomFixture(packages) {
  return {
    spdxVersion: 'SPDX-2.3',
    dataLicense: 'CC0-1.0',
    documentNamespace: 'https://example.test/sbom',
    creationInfo: {
      created: '2026-07-24T00:00:00Z',
      creators: ['Tool: test'],
    },
    packages,
    relationships: [{
      spdxElementId: 'SPDXRef-DOCUMENT',
      relationshipType: 'DESCRIBES',
      relatedSpdxElement: packages[0].SPDXID,
    }],
  };
}

function enrichmentPolicy() {
  return {
    allowedExpressions: ['AGPL-3.0-only', 'MIT'],
    deniedValues: ['', 'NONE', 'NOASSERTION', 'UNKNOWN', 'UNLICENSED'],
    reviewedEvidence: [],
    reviewedExceptions: [],
    sbom: {
      deniedLicensePatterns: ['LicenseRef-.*[Pp]roprietary'],
      distroAliases: [{
        name: 'openssl',
        licenseSourcePackage: 'libssl3t64',
      }],
      packageEvidence: [],
      reviewedEcosystems: [{
        purlPrefixes: ['pkg:deb/debian/'],
        evidence: 'Fixture distribution evidence',
        reviewer: 'Maintainer',
        reviewDate: '2026-07-24',
        reviewAfter: '2099-07-24',
        rationale: 'Fixture distribution packages remain separately licensed.',
      }],
      runtimePackageEvidence: [],
    },
  };
}

test('validateProvenanceManifest accepts an approved immutable derived file', async () => {
  await withFixture(async ({ root, manifest }) => {
    assert.deepEqual(await validateProvenanceManifest(root, manifest), []);
  });
});

test('validateProvenanceManifest rejects an unknown mutable source revision', async () => {
  await withFixture(async ({ root, manifest }) => {
    manifest.approvedSources[0].commitSha = 'main';
    manifest.approvedSources[0].tag = 'latest';
    const errors = await validateProvenanceManifest(root, manifest);
    assert.ok(hasCode(errors, 'PROVENANCE_SOURCE_COMMIT'));
    assert.ok(hasCode(errors, 'PROVENANCE_SOURCE_TAG'));
  });
});

test('validateProvenanceManifest rejects a source with an incompatible license', async () => {
  await withFixture(async ({ root, manifest }) => {
    manifest.approvedSources[0].licenseExpression = 'GPL-2.0-only';
    const errors = await validateProvenanceManifest(root, manifest);
    assert.ok(hasCode(errors, 'PROVENANCE_SOURCE_LICENSE'));
  });
});

test('validateProvenanceManifest executes the schema and rejects extra properties', async () => {
  await withFixture(async ({ root, manifest }) => {
    manifest.unreviewedField = true;
    const errors = await validateProvenanceManifest(root, manifest);
    assert.ok(hasCode(errors, 'PROVENANCE_SCHEMA'));
  });
});

test('validateProvenanceManifest rejects a governed file without a manifest entry', async () => {
  await withFixture(async ({ root, manifest }) => {
    manifest.entries = [];
    const errors = await validateProvenanceManifest(root, manifest);
    assert.ok(hasCode(errors, 'PROVENANCE_MANIFEST_ENTRY_MISSING'));
    assert.ok(hasCode(errors, 'PROVENANCE_MARKER_UNREGISTERED'));
  });
});

test('validateProvenanceManifest rejects a changed derived destination', async () => {
  await withFixture(async ({ root, manifest, destinationPath }) => {
    await writeFile(path.join(root, destinationPath), 'changed\n');
    const errors = await validateProvenanceManifest(root, manifest);
    assert.ok(hasCode(errors, 'PROVENANCE_DESTINATION_DIGEST'));
  });
});

test('validateProvenanceManifest rejects a missing preserved notice', async () => {
  await withFixture(async ({ root, manifest }) => {
    manifest.entries[0].preservedNotices = ['Missing upstream notice'];
    const errors = await validateProvenanceManifest(root, manifest);
    assert.ok(hasCode(errors, 'PROVENANCE_NOTICE_PRESERVATION'));
  });
});

test('validateProvenanceManifest rejects an invalid source blob digest', async () => {
  await withFixture(async ({ root, manifest }) => {
    manifest.entries[0].sourceBlobDigest = 'sha1:invalid';
    const errors = await validateProvenanceManifest(root, manifest);
    assert.ok(hasCode(errors, 'PROVENANCE_BLOB_DIGEST'));
  });
});

test('validateProvenanceManifest rejects static printer data', async () => {
  await withFixture(async ({ root, manifest, content }) => {
    const destinationPath = 'src/Farm/Calibration/Derived/static-printer-catalog.cs';
    await writeFile(path.join(root, destinationPath), content);
    manifest.entries[0].destinationPath = destinationPath;
    const errors = await validateProvenanceManifest(root, manifest);
    assert.ok(hasCode(errors, 'PROVENANCE_EXCLUDED_CONTENT'));
  });
});

test('validateProvenanceManifest rejects an unverified asset', async () => {
  await withFixture(async ({ root, manifest, content }) => {
    const destinationPath = 'src/Farm/Calibration/Derived/calibration-model.stl';
    await writeFile(path.join(root, destinationPath), content);
    manifest.entries[0].destinationPath = destinationPath;
    const errors = await validateProvenanceManifest(root, manifest);
    assert.ok(hasCode(errors, 'PROVENANCE_EXCLUDED_CONTENT'));
  });
});

test('validateLicenseMetadata rejects conflicting first-party metadata', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'printfarmer-license-'));
  try {
    await writeFile(path.join(root, 'LICENSE'), 'canonical\n');
    await writeFile(path.join(root, 'VERSION'), 'v0.2.3\n');
    await writeFile(path.join(root, 'README.md'), 'licensed under the MIT License\n');
    const policy = {
      licenseExpression: 'AGPL-3.0-only',
      canonicalLicense: {
        path: 'LICENSE',
        normalizedSha256: sha256('canonical\n'),
      },
      decision: {
        effectiveVersion: 'v0.2.3',
      },
      requiredFiles: [],
      packageManifests: [],
      metadataAssertions: [],
      forbiddenFirstPartyDeclarations: ['licensed under the MIT License'],
      forbiddenScanPaths: ['README.md'],
      exceptions: [],
    };
    const errors = await validateLicenseMetadata(root, policy);
    assert.ok(hasCode(errors, 'LICENSE_CONFLICT'));
  } finally {
    await rm(root, { force: true, recursive: true });
  }
});

test('validateLicenseMetadata rejects prohibited release workflow metadata', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'printfarmer-workflow-policy-'));
  try {
    await writeFile(path.join(root, 'LICENSE'), 'canonical\n');
    await writeFile(path.join(root, 'VERSION'), 'v0.2.3\n');
    await writeFile(path.join(root, 'release.yml'), 'required gate\nduplicate workflow invocation\n');
    const policy = {
      licenseExpression: 'AGPL-3.0-only',
      canonicalLicense: {
        path: 'LICENSE',
        normalizedSha256: sha256('canonical\n'),
      },
      decision: {
        effectiveVersion: 'v0.2.3',
      },
      requiredFiles: [],
      packageManifests: [],
      metadataAssertions: [{
        path: 'release.yml',
        contains: ['required gate'],
        notContains: ['duplicate workflow invocation'],
      }],
      forbiddenFirstPartyDeclarations: [],
      forbiddenScanPaths: [],
      exceptions: [],
    };
    const errors = await validateLicenseMetadata(root, policy);
    assert.ok(hasCode(errors, 'METADATA_REJECTION'));
  } finally {
    await rm(root, { force: true, recursive: true });
  }
});

test('validateLicenseMetadata rejects release gates in the wrong order', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'printfarmer-workflow-order-'));
  try {
    await writeFile(path.join(root, 'LICENSE'), 'canonical\n');
    await writeFile(path.join(root, 'VERSION'), 'v0.2.3\n');
    await writeFile(path.join(root, 'release.yml'), 'validate\npromote\npublish source\n');
    const policy = {
      licenseExpression: 'AGPL-3.0-only',
      canonicalLicense: {
        path: 'LICENSE',
        normalizedSha256: sha256('canonical\n'),
      },
      decision: {
        effectiveVersion: 'v0.2.3',
      },
      requiredFiles: [],
      packageManifests: [],
      metadataAssertions: [{
        path: 'release.yml',
        orderedContains: ['validate', 'publish source', 'promote'],
      }],
      forbiddenFirstPartyDeclarations: [],
      forbiddenScanPaths: [],
      exceptions: [],
    };
    const errors = await validateLicenseMetadata(root, policy);
    assert.ok(hasCode(errors, 'METADATA_ORDER'));
  } finally {
    await rm(root, { force: true, recursive: true });
  }
});

test('validateLicenseMetadata rejects changed preserved third-party terms', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'printfarmer-vendored-license-'));
  try {
    await writeFile(path.join(root, 'LICENSE'), 'canonical\n');
    await writeFile(path.join(root, 'VERSION'), 'v0.2.3\n');
    await writeFile(path.join(root, 'vendored-license.txt'), 'changed terms\n');
    await writeFile(path.join(root, 'component.sh'), '#!/bin/sh\n');
    const policy = {
      licenseExpression: 'AGPL-3.0-only',
      canonicalLicense: {
        path: 'LICENSE',
        normalizedSha256: sha256('canonical\n'),
      },
      decision: {
        effectiveVersion: 'v0.2.3',
      },
      requiredFiles: [],
      preservedThirdPartyLicenses: [{
        path: 'vendored-license.txt',
        sha256: sha256('expected terms\n'),
        source: 'https://example.com/license',
        appliesTo: ['component.sh'],
      }],
      packageManifests: [],
      metadataAssertions: [],
      forbiddenFirstPartyDeclarations: [],
      forbiddenScanPaths: [],
      exceptions: [],
    };
    const errors = await validateLicenseMetadata(root, policy);
    assert.ok(hasCode(errors, 'THIRD_PARTY_LICENSE_CHANGED'));
  } finally {
    await rm(root, { force: true, recursive: true });
  }
});

test('validateSbomDocument rejects missing component licenses', () => {
  const dependencyPolicy = {
    allowedExpressions: ['MIT'],
    deniedValues: ['', 'NONE', 'NOASSERTION', 'UNKNOWN', 'UNLICENSED'],
    reviewedExceptions: [],
  };
  const sbom = {
    spdxVersion: 'SPDX-2.3',
    dataLicense: 'CC0-1.0',
    documentNamespace: 'https://example.test/sbom',
    creationInfo: {
      created: '2026-07-24T00:00:00Z',
      creators: ['Tool: test'],
    },
    packages: [{
      SPDXID: 'SPDXRef-Package',
      name: 'unknown-component',
      versionInfo: '1.0.0',
      downloadLocation: 'https://example.test/component',
      licenseDeclared: 'NOASSERTION',
    }],
    relationships: [{
      spdxElementId: 'SPDXRef-DOCUMENT',
      relationshipType: 'DESCRIBES',
      relatedSpdxElement: 'SPDXRef-Package',
    }],
  };
  const errors = validateSbomDocument(sbom, 'sbom.spdx.json', dependencyPolicy);
  assert.ok(hasCode(errors, 'LICENSE_UNKNOWN'));
});

test('validateSbomDocument requires extracted text for custom licenses', () => {
  const customLicense = 'LicenseRef-Microsoft-SNI-Distributable-Code';
  const policy = {
    allowedExpressions: [customLicense],
    deniedValues: ['', 'NONE', 'NOASSERTION', 'UNKNOWN', 'UNLICENSED'],
    reviewedExceptions: [],
    sbom: {
      deniedLicensePatterns: [],
      reviewedEcosystems: [],
    },
  };
  const component = sbomPackage(
    'SPDXRef-CustomLicense',
    'custom-component',
    '1.0.0',
    'pkg:generic/custom-component@1.0.0',
    { licenseDeclared: customLicense },
  );
  const sbom = sbomFixture([component]);
  assert.ok(hasCode(
    validateSbomDocument(sbom, 'sbom.spdx.json', policy),
    'LICENSE_UNREVIEWED',
  ));

  sbom.hasExtractedLicensingInfos = [{
    extractedText: 'Reviewed custom license terms.',
    licenseId: customLicense,
    name: 'Reviewed custom license',
  }];
  assert.deepEqual(validateSbomDocument(sbom, 'sbom.spdx.json', policy), []);
});

test('scanPublicationFiles rejects embedded credentials', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'printfarmer-publication-'));
  try {
    await writeFile(path.join(root, 'source.json'), '{"token":"ghp_abcdefghijklmnopqrstuvwxyz"}\n');
    const pattern = 'gh[pousr]_[A-Za-z0-9]{20,}';
    const errors = await scanPublicationFiles(root, ['source.json'], [pattern]);
    assert.ok(hasCode(errors, 'PUBLICATION_SECRET'));

    assert.deepEqual(
      await scanPublicationFiles(root, ['source.json'], [pattern], [{
        matchSha256: 'd66f4cc7d7c08ec57ca73717f8625478602fd28781494d2876f871b01f4f35b9',
        path: 'source.json',
        pattern,
        reason: 'Synthetic negative-test fixture.',
      }]),
      [],
    );

    const staleErrors = await scanPublicationFiles(root, ['source.json'], [pattern], [{
      matchSha256: '0000000000000000000000000000000000000000000000000000000000000000',
      path: 'source.json',
      pattern,
      reason: 'Deliberately stale fixture.',
    }]);
    assert.ok(hasCode(staleErrors, 'PUBLICATION_SECRET'));
    assert.ok(hasCode(staleErrors, 'PUBLICATION_SECRET_EXCEPTION_STALE'));
  } finally {
    await rm(root, { force: true, recursive: true });
  }
});

test('scanPublicationFiles permits variable credential templates', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'printfarmer-publication-template-'));
  try {
    await writeFile(
      path.join(root, 'source.txt'),
      'upstream=https://x-access-token:${PUBLIC_REPO_PAT}@example.test/source\n',
    );
    assert.deepEqual(
      await scanPublicationFiles(
        root,
        ['source.txt'],
        ['https?://[^\\s/:]+:[^\\s/@]+@'],
      ),
      [],
    );
  } finally {
    await rm(root, { force: true, recursive: true });
  }
});

test('scanSourceArchive rejects secrets in the exact archived contents', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'printfarmer-source-archive-'));
  const payloadDirectory = path.join(root, 'PrintFarmer-v1.0.0');
  const archivePath = path.join(root, 'source.tar.gz');
  try {
    await mkdir(payloadDirectory);
    await writeFile(
      path.join(payloadDirectory, 'configuration.txt'),
      'upstream=https://build-user:embedded-password@example.test/source\n',
    );
    await execFileAsync('tar', [
      '-czf',
      archivePath,
      '-C',
      root,
      path.basename(payloadDirectory),
    ]);

    await assert.rejects(
      scanSourceArchive(archivePath, ['https?://[^\\s/:]+:[^\\s/@]+@']),
      /PUBLICATION_SECRET/,
    );
  } finally {
    await rm(root, { force: true, recursive: true });
  }
});

test('findNugetAssetsFiles discovers restored assets under obj directories', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'printfarmer-nuget-assets-'));
  try {
    const assetsDirectory = path.join(root, 'src', 'Example', 'obj');
    await mkdir(assetsDirectory, { recursive: true });
    await writeFile(path.join(assetsDirectory, 'project.assets.json'), '{}\n');

    assert.deepEqual(
      await findNugetAssetsFiles(root),
      ['src/Example/obj/project.assets.json'],
    );
  } finally {
    await rm(root, { force: true, recursive: true });
  }
});

test('createNugetLicenseInventory records restored production packages and projects', async () => {
  const fixture = await createNugetLicenseFixture(
    '<license type="expression">MIT</license>',
  );
  try {
    const result = await createNugetLicenseInventory(fixture.root, nugetPolicy([]));
    assert.deepEqual(result.errors, []);
    assert.deepEqual(result.inventory.projects, [{
      name: 'app',
      path: path.join(fixture.root, 'src', 'app', 'app.csproj').replaceAll('\\', '/'),
    }]);
    assert.deepEqual(result.inventory.packages, [{
      files: [],
      licenseExpression: 'MIT',
      name: fixture.packageName,
      observedLicense: 'MIT',
      purl: 'pkg:nuget/Example.Package@1.0.0',
      version: fixture.version,
    }]);
  } finally {
    await rm(fixture.root, { force: true, recursive: true });
  }
});

test('createNpmLicenseInventory includes production packages only', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'printfarmer-npm-license-'));
  const lockPath = path.join(root, 'src', 'Web', 'ReactApp', 'package-lock.json');
  try {
    await mkdir(path.dirname(lockPath), { recursive: true });
    await writeFile(lockPath, JSON.stringify({
      lockfileVersion: 3,
      name: 'fixture',
      packages: {
        '': {
          license: 'AGPL-3.0-only',
          name: 'fixture',
          version: '1.0.0',
        },
        'node_modules/dev-only': {
          dev: true,
          license: 'MIT',
          version: '1.0.0',
        },
        'node_modules/optional-only': {
          license: 'MIT',
          optional: true,
          version: '1.0.0',
        },
        'node_modules/runtime': {
          integrity: 'sha512-fixture',
          license: 'MIT',
          resolved: 'https://registry.npmjs.org/runtime/-/runtime-1.0.0.tgz',
          version: '1.0.0',
        },
      },
    }));
    const policy = {
      allowedExpressions: ['MIT'],
      deniedValues: ['', 'NONE', 'NOASSERTION', 'UNKNOWN', 'UNLICENSED'],
      npm: {
        licenseTextFallbacks: [],
      },
      reviewedExceptions: [],
      sbom: {
        npmBundleLockFile: 'src/Web/ReactApp/package-lock.json',
      },
    };
    const result = await createNpmLicenseInventory(root, policy);
    assert.deepEqual(result.errors, []);
    assert.deepEqual(result.packages.map((record) => record.purl), ['pkg:npm/runtime@1.0.0']);
  } finally {
    await rm(root, { force: true, recursive: true });
  }
});

test('enrichSbomDocument resolves first-party, NuGet, native, and distro evidence', () => {
  const revision = '0123456789abcdef0123456789abcdef01234567';
  const inventory = {
    schemaVersion: 1,
    revision,
    version: 'v0.2.3',
    projects: [{
      name: 'Farm.Web.Api',
      path: 'src/api/Farm.Web.Api.csproj',
    }],
    packages: [{
      files: [
        'lib/net10.0/Example.Package.dll',
        'runtimes/linux-x64/native/libexample.so',
      ],
      licenseExpression: 'MIT',
      name: 'Example.Package',
      observedLicense: 'MIT',
      purl: 'pkg:nuget/Example.Package@1.0.0',
      version: '1.0.0',
    }],
  };
  const packages = [
    sbomPackage('SPDXRef-DocumentRoot-image', 'printfarmer-image', 'sha256:test'),
    sbomPackage('SPDXRef-Api', 'Farm.Web.Api', '1.0.0'),
    sbomPackage(
      'SPDXRef-NuGet',
      'Example.Package',
      '1.0.0.0',
      'pkg:nuget/Example.Package@1.0.0',
    ),
    sbomPackage(
      'SPDXRef-Native',
      'libexample.so',
      'UNKNOWN',
      'pkg:generic/libexample.so@UNKNOWN',
      { sourceInfo: 'paths: /app/runtimes/linux-x64/native/libexample.so' },
    ),
    sbomPackage(
      'SPDXRef-LibSsl',
      'libssl3t64',
      '3.0.1',
      'pkg:deb/debian/libssl3t64@3.0.1',
      {
        licenseDeclared: 'LicenseRef-Debian-OpenSSL',
        sourceInfo: 'paths: /usr/share/doc/libssl3t64/copyright',
      },
    ),
    sbomPackage(
      'SPDXRef-OpenSslDistro',
      'openssl',
      '3.0.1',
      'pkg:deb/debian/openssl@3.0.1',
    ),
    sbomPackage(
      'SPDXRef-OpenSslBinary',
      'openssl',
      '3.0.1',
      'pkg:generic/openssl@3.0.1',
    ),
  ];
  const sbom = {
    ...sbomFixture(packages),
    hasExtractedLicensingInfos: [{
      licenseId: 'LicenseRef-Debian-OpenSSL',
      name: 'Debian OpenSSL copyright evidence',
      extractedText: 'NOASSERTION',
    }],
  };
  const policy = enrichmentPolicy();
  const errors = enrichSbomDocument(sbom, inventory, policy, {
    inventoryPath: 'license-inventory.json',
    licenseExpression: 'AGPL-3.0-only',
    repositoryUrl: 'https://github.com/OlyForge3D/PrintFarmer',
    revision,
    version: 'v0.2.3',
  });

  assert.deepEqual(errors, []);
  assert.deepEqual(validateSbomDocument(sbom, 'sbom.spdx.json', policy), []);
  assert.equal(packages[0].licenseDeclared, 'AGPL-3.0-only');
  assert.equal(packages[1].licenseDeclared, 'AGPL-3.0-only');
  assert.equal(packages[2].licenseDeclared, 'MIT');
  assert.equal(packages[3].licenseDeclared, 'MIT');
  assert.equal(packages[3].versionInfo, '1.0.0');
  assert.equal(packages[5].externalRefs[0].referenceLocator, 'pkg:deb/debian/openssl@3.0.1');
  assert.equal(packages[6].externalRefs[0].referenceLocator, 'pkg:deb/debian/openssl@3.0.1');
});

test('enrichSbomDocument rejects a NuGet package with a mismatched version', () => {
  const revision = validCommit;
  const inventory = {
    packages: [{
      files: [],
      licenseExpression: 'MIT',
      name: 'Example.Package',
      observedLicense: 'MIT',
      purl: 'pkg:nuget/Example.Package@1.0.0',
      version: '1.0.0',
    }],
    projects: [],
    revision,
    schemaVersion: 1,
    version: 'v0.2.3',
  };
  const component = sbomPackage(
    'SPDXRef-NuGet',
    'Example.Package',
    '2.0.0',
    'pkg:nuget/Example.Package@2.0.0',
  );
  const sbom = sbomFixture([
    sbomPackage('SPDXRef-DocumentRoot-image', 'printfarmer-image', 'sha256:test'),
    component,
  ]);
  const errors = enrichSbomDocument(sbom, inventory, enrichmentPolicy(), {
    inventoryPath: 'license-inventory.json',
    licenseExpression: 'AGPL-3.0-only',
    repositoryUrl: 'https://github.com/OlyForge3D/PrintFarmer',
    revision,
    version: 'v0.2.3',
  });

  assert.deepEqual(errors, []);
  assert.ok(hasCode(
    validateSbomDocument(sbom, 'sbom.spdx.json', enrichmentPolicy()),
    'LICENSE_UNKNOWN',
  ));
  assert.equal(component.licenseDeclared, 'NOASSERTION');
});

test('enrichSbomDocument injects production npm dependencies when requested', () => {
  const revision = validCommit;
  const inventory = {
    npmPackages: [{
      integrity: 'sha512-fixture',
      licenseExpression: 'MIT',
      name: 'runtime',
      purl: 'pkg:npm/runtime@1.0.0',
      resolved: 'https://registry.npmjs.org/runtime/-/runtime-1.0.0.tgz',
      version: '1.0.0',
    }],
    packages: [],
    projects: [],
    revision,
    schemaVersion: 1,
    version: 'v0.2.3',
  };
  const sbom = sbomFixture([
    sbomPackage('SPDXRef-DocumentRoot-image', 'printfarmer-image', 'sha256:test'),
  ]);
  const errors = enrichSbomDocument(sbom, inventory, enrichmentPolicy(), {
    includeNpm: true,
    inventoryPath: 'license-inventory.json',
    licenseExpression: 'AGPL-3.0-only',
    repositoryUrl: 'https://github.com/OlyForge3D/PrintFarmer',
    revision,
    version: 'v0.2.3',
  });

  assert.deepEqual(errors, []);
  const npmPackage = sbom.packages.find((record) => record.name === 'runtime');
  assert.equal(npmPackage.licenseDeclared, 'MIT');
  assert.equal(npmPackage.externalRefs[0].referenceLocator, 'pkg:npm/runtime@1.0.0');
  assert.ok(sbom.relationships.some((relationship) =>
    relationship.relatedSpdxElement === npmPackage.SPDXID
    && relationship.relationshipType === 'CONTAINS'));
});

test('SBOM enrichment remains fail-closed for unmatched and opaque components', () => {
  const policy = enrichmentPolicy();
  const sbom = sbomFixture([
    sbomPackage(
      'SPDXRef-Unmatched',
      'Unmatched.Package',
      '1.0.0',
      'pkg:nuget/Unmatched.Package@1.0.0',
    ),
    sbomPackage('SPDXRef-Opaque', 'opaque-binary', '1.0.0'),
  ]);
  const errors = validateSbomDocument(sbom, 'sbom.spdx.json', policy);

  assert.ok(hasCode(errors, 'LICENSE_UNKNOWN'));
  assert.ok(hasCode(errors, 'SBOM_COMPONENT_SOURCE'));
});

test('validateDependencyLicenses accepts only the reviewed NuGet license file hash', async () => {
  const fixture = await createNugetLicenseFixture(
    '<license type="file">LICENSE.txt</license>',
    'reviewed license text\n',
  );
  try {
    const observedLicense = `FILE-SHA256:${sha256(Buffer.from('reviewed license text\n'))}`;
    const policy = nugetPolicy([{
      ecosystem: 'nuget',
      observedLicense,
      approvedExpression: 'MIT',
      evidence: 'Test package LICENSE.txt',
      reviewer: 'Maintainer',
      reviewDate: '2026-07-24',
      reviewAfter: '2099-07-24',
      rationale: 'Fixture for exact-content review.',
    }]);
    assert.deepEqual(await validateDependencyLicenses(fixture.root, policy), []);

    await writeFile(path.join(fixture.packagePath, 'LICENSE.txt'), 'changed license text\n');
    const changedErrors = await validateDependencyLicenses(fixture.root, policy);
    assert.ok(hasCode(changedErrors, 'LICENSE_UNREVIEWED'));
  } finally {
    await rm(fixture.root, { force: true, recursive: true });
  }
});

test('validateDependencyLicenses accepts only the reviewed legacy NuGet license URL', async () => {
  const reviewedUrl = 'https://licenses.example.test/immutable-license';
  const fixture = await createNugetLicenseFixture(`<licenseUrl>${reviewedUrl}</licenseUrl>`);
  try {
    const policy = nugetPolicy([{
      ecosystem: 'nuget',
      observedLicense: `URL:${reviewedUrl}`,
      approvedExpression: 'MIT',
      evidence: 'Immutable test license URL',
      reviewer: 'Maintainer',
      reviewDate: '2026-07-24',
      reviewAfter: '2099-07-24',
      rationale: 'Fixture for exact-URL review.',
    }]);
    assert.deepEqual(await validateDependencyLicenses(fixture.root, policy), []);

    await writeFile(
      path.join(fixture.packagePath, `${fixture.packageName.toLowerCase()}.nuspec`),
      '<package><metadata><id>Example.Package</id><version>1.0.0</version><licenseUrl>https://licenses.example.test/changed</licenseUrl></metadata></package>',
    );
    const changedErrors = await validateDependencyLicenses(fixture.root, policy);
    assert.ok(hasCode(changedErrors, 'LICENSE_UNREVIEWED'));
  } finally {
    await rm(fixture.root, { force: true, recursive: true });
  }
});
