const webSourceExtensions = new Set([
  '.astro',
  '.css',
  '.htm',
  '.html',
  '.jpeg',
  '.jpg',
  '.js',
  '.jsx',
  '.less',
  '.png',
  '.sass',
  '.scss',
  '.svelte',
  '.svg',
  '.ts',
  '.tsx',
  '.vue',
  '.webp',
]);

const webExcludedSegments = new Set([
  '__fixtures__',
  '__tests__',
  'build',
  'coverage',
  'dist',
  'fixtures',
  'node_modules',
  'test',
  'tests',
  'vendor',
]);

const webPublicImageExtensions = new Set([
  '.avif',
  '.gif',
  '.ico',
  '.jpeg',
  '.jpg',
  '.png',
  '.svg',
  '.webp',
]);

const webPublicIdentityFiles = new Set([
  'favicon-16x16.png',
  'favicon.png',
  'manifest.webmanifest',
  'printables-icon.png',
  'printfarmer-logo.svg',
]);

const sharedDesignFiles = new Set([
  'mobile/.impeccable/design.json',
  'mobile/AGENTS.md',
  'mobile/DESIGN.md',
  '.github/copilot-instructions.md',
  '.github/pull_request_template.md',
  '.github/agents/design-reviewer.agent.md',
  '.github/hooks/impeccable.json',
  '.github/workflows/ci.yml',
  '.github/skills/reviewer-protocol/SKILL.md',
  '.github/workflows/design-detector.yml',
  '.github/workflows/squad-review-verdict.yml',
  '.impeccable/config.json',
  '.impeccable/design.json',
  '.squad/agents/bishop/charter.md',
  '.squad/agents/dallas/charter.md',
  '.squad/agents/design-reviewer/charter.md',
  '.squad/agents/hicks/charter.md',
  '.squad/agents/vasquez/charter.md',
  '.squad/ceremonies.md',
  '.squad/ralph-instructions.md',
  '.squad/routing.md',
  '.squad/skills/reviewer-protocol/SKILL.md',
  'PRODUCT.md',
  'mobile/PRODUCT.md',
  'DESIGN.json',
  'DESIGN.md',
  'scripts/ci/squad-verdict-gate.mjs',
  'scripts/ci/tests/test-design-ci-integration.mjs',
  'scripts/ci/tests/test-squad-verdict-gate.mjs',
  'scripts/ci/verify-squad-verdict.mjs',
  'scripts/design/',
]);

const nativeAssetExtensions = new Set([
  '.colorset',
  '.imageset',
  '.pdf',
  '.png',
  '.jpg',
  '.jpeg',
  '.svg',
  '.webp',
]);

function normalizePath(filePath) {
  return String(filePath ?? '').replaceAll('\\', '/').replace(/^\.\/+/, '');
}

function hasExcludedWebSegment(filePath) {
  const segments = filePath.split('/');
  return segments.some(segment => webExcludedSegments.has(segment)) ||
    segments.some(segment => /\.(?:test|spec)\.[^.]+$/i.test(segment));
}

function isWebUiPath(filePath) {
  const webIndex = 'src/Web/ReactApp/index.html';
  if (filePath === webIndex) return true;

  const publicPrefix = 'src/Web/ReactApp/public/';
  if (filePath.startsWith(publicPrefix)) {
    const publicPath = filePath.slice(publicPrefix.length);
    if (webPublicIdentityFiles.has(publicPath)) return true;
    const extension = filePath.slice(filePath.lastIndexOf('.')).toLowerCase();
    if (publicPath.startsWith('icons/') &&
      !publicPath.startsWith('icons/orca/') &&
      webPublicImageExtensions.has(extension)) return true;
    return publicPath.startsWith('assets/printers/') &&
      webPublicImageExtensions.has(extension);
  }

  const webSourcePrefix = 'src/Web/ReactApp/src/';
  if (!filePath.startsWith(webSourcePrefix) || hasExcludedWebSegment(filePath)) {
    return false;
  }

  return webSourceExtensions.has(filePath.slice(filePath.lastIndexOf('.')).toLowerCase());
}

function isNativeUiPath(filePath) {
  const nativePrefix = 'mobile/PrintFarmer/';
  if (!filePath.startsWith(nativePrefix)) return false;

  const relativePath = filePath.slice(nativePrefix.length);
  const segments = relativePath.split('/');
  if (segments.some(segment => webExcludedSegments.has(segment))) return false;

  if (segments[0] === 'Views' || segments[0] === 'Theme' ||
    /(?:^|\/)[^/]*View\.swift$/i.test(relativePath)) {
    return filePath.toLowerCase().endsWith('.swift');
  }

  const extension = filePath.slice(filePath.lastIndexOf('.')).toLowerCase();
  return filePath.includes('.xcassets/') &&
    (extension === '.json' || nativeAssetExtensions.has(extension));
}

function isSharedDesignPath(filePath) {
  if (sharedDesignFiles.has(filePath)) return true;
  if (filePath.startsWith('scripts/design/')) return true;
  return false;
}

export function isUiPath(filePath) {
  const normalizedPath = normalizePath(filePath);
  return isWebUiPath(normalizedPath) ||
    isNativeUiPath(normalizedPath) ||
    isSharedDesignPath(normalizedPath);
}
