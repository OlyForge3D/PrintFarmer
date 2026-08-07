#!/usr/bin/env node

import {
  lstat,
  readFile,
  readdir,
} from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const headingDisallowedFenceLanguages = new Set([
  'cs',
  'csharp',
  'css',
  'html',
  'javascript',
  'js',
  'json',
  'jsx',
  'scss',
  'sql',
  'ts',
  'tsx',
  'typescript',
  'xml',
]);

function normalizeRelative(filePath) {
  return filePath.split(path.sep).join('/');
}

function stripInlineCode(line) {
  return line.replace(/(`+)(.*?)\1/g, '');
}

function extractInlineDestinations(line) {
  const destinations = [];

  for (let index = 0; index < line.length - 1; index += 1) {
    if (line[index] !== ']' || line[index + 1] !== '(' || line[index - 1] === '\\') {
      continue;
    }

    let cursor = index + 2;
    while (/\s/.test(line[cursor] ?? '')) {
      cursor += 1;
    }

    if (line[cursor] === '<') {
      const end = line.indexOf('>', cursor + 1);
      if (end !== -1) {
        destinations.push(line.slice(cursor + 1, end));
        index = end;
      }
      continue;
    }

    const start = cursor;
    let nestedParentheses = 0;
    let escaped = false;
    while (cursor < line.length) {
      const character = line[cursor];
      if (escaped) {
        escaped = false;
      } else if (character === '\\') {
        escaped = true;
      } else if (character === '(') {
        nestedParentheses += 1;
      } else if (character === ')') {
        if (nestedParentheses === 0) {
          break;
        }
        nestedParentheses -= 1;
      } else if (/\s/.test(character) && nestedParentheses === 0) {
        break;
      }
      cursor += 1;
    }

    if (cursor > start) {
      destinations.push(line.slice(start, cursor).replaceAll('\\)', ')'));
      index = cursor;
    }
  }

  return destinations;
}

function scanMarkdown(source, sourcePath) {
  const links = [];
  const issues = [];
  const headingLines = [];
  const explicitAnchors = [];
  const lines = source.split(/\r?\n/);
  let fence;

  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index];
    const lineNumber = index + 1;

    if (fence) {
      const closingFence = line.match(/^\s{0,3}(`+|~+)\s*$/);
      if (
        closingFence
        && closingFence[1][0] === fence.character
        && closingFence[1].length >= fence.length
      ) {
        fence = undefined;
        continue;
      }

      if (
        headingDisallowedFenceLanguages.has(fence.language)
        && /^\s{0,3}#{1,6}\s+\S/.test(line)
      ) {
        issues.push({
          file: sourcePath,
          line: lineNumber,
          type: 'markdown-heading-in-code-fence',
          detail: `Markdown heading syntax appears inside a ${fence.language} code fence`,
        });
      }
      continue;
    }

    const openingFence = line.match(/^\s{0,3}(`{3,}|~{3,})\s*([^ ]*)?.*$/);
    if (openingFence) {
      fence = {
        character: openingFence[1][0],
        length: openingFence[1].length,
        language: (openingFence[2] ?? '').toLowerCase(),
        line: lineNumber,
      };
      continue;
    }

    const contentLine = stripInlineCode(line);
    const atxHeading = contentLine.match(/^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$/);
    if (atxHeading) {
      headingLines.push(atxHeading[1]);
    } else if (
      index + 1 < lines.length
      && contentLine.trim()
      && /^\s{0,3}(=+|-+)\s*$/.test(lines[index + 1])
    ) {
      headingLines.push(contentLine.trim());
    }

    for (const match of contentLine.matchAll(/<a\s+[^>]*(?:id|name)=["']([^"']+)["'][^>]*>/gi)) {
      explicitAnchors.push(match[1]);
    }

    for (const destination of extractInlineDestinations(contentLine)) {
      links.push({ destination, line: lineNumber });
    }

    const referenceDefinition = contentLine.match(
      /^\s{0,3}\[[^\]]+\]:\s*(?:<([^>]+)>|(\S+))/,
    );
    if (referenceDefinition) {
      links.push({
        destination: referenceDefinition[1] ?? referenceDefinition[2],
        line: lineNumber,
      });
    }
  }

  if (fence) {
    issues.push({
      file: sourcePath,
      line: fence.line,
      type: 'unclosed-code-fence',
      detail: `Code fence opened on line ${fence.line} is not closed`,
    });
  }

  return {
    explicitAnchors,
    headingLines,
    issues,
    links,
  };
}

function githubSlug(heading) {
  return heading
    .replace(/<[^>]*>/g, '')
    .replace(/!\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/\[([^\]]+)\]\([^)]*\)/g, '$1')
    .replace(/&amp;/gi, '&')
    .replace(/&lt;/gi, '<')
    .replace(/&gt;/gi, '>')
    .replace(/[`*_~]/g, '')
    .trim()
    .toLowerCase()
    .replace(/[^\p{L}\p{N}\s_-]/gu, '')
    .replace(/\s/g, '-');
}

function buildAnchorSet(scan) {
  const anchors = new Set(scan.explicitAnchors);
  const slugCounts = new Map();

  for (const heading of scan.headingLines) {
    const baseSlug = githubSlug(heading);
    const duplicateIndex = slugCounts.get(baseSlug) ?? 0;
    slugCounts.set(baseSlug, duplicateIndex + 1);
    anchors.add(duplicateIndex === 0 ? baseSlug : `${baseSlug}-${duplicateIndex}`);
  }

  return anchors;
}

function isSkippedDestination(destination) {
  return (
    destination.startsWith('//')
    || /^[a-z][a-z0-9+.-]*:/i.test(destination)
  );
}

async function inspectCaseSensitivePath(repositoryRoot, targetPath) {
  const relativeTarget = path.relative(repositoryRoot, targetPath);
  if (
    relativeTarget === '..'
    || relativeTarget.startsWith(`..${path.sep}`)
    || path.isAbsolute(relativeTarget)
  ) {
    return { ok: false, reason: 'target escapes the repository root' };
  }

  let currentPath = repositoryRoot;
  for (const component of relativeTarget.split(path.sep).filter(Boolean)) {
    let entries;
    try {
      entries = await readdir(currentPath);
    } catch {
      return { ok: false, reason: `missing path component '${component}'` };
    }

    if (entries.includes(component)) {
      currentPath = path.join(currentPath, component);
      continue;
    }

    const caseInsensitiveMatch = entries.find(
      (entry) => entry.toLowerCase() === component.toLowerCase(),
    );
    if (caseInsensitiveMatch) {
      return {
        ok: false,
        reason: `case mismatch: wrote '${component}', repository has '${caseInsensitiveMatch}'`,
      };
    }

    return { ok: false, reason: `missing path component '${component}'` };
  }

  return { ok: true, path: currentPath };
}

async function resolveTarget(repositoryRoot, sourcePath, rawPath) {
  const decodedPath = decodeURIComponent(rawPath);
  const targetPath = decodedPath.startsWith('/')
    ? path.resolve(repositoryRoot, decodedPath.slice(1))
    : path.resolve(path.dirname(sourcePath), decodedPath || '.');
  const inspected = await inspectCaseSensitivePath(repositoryRoot, targetPath);
  if (!inspected.ok) {
    return inspected;
  }

  const targetStats = await lstat(inspected.path);
  if (!targetStats.isDirectory()) {
    return inspected;
  }

  const entries = await readdir(inspected.path);
  const indexName = ['README.md', 'index.md', 'INDEX.md'].find((name) => entries.includes(name));
  if (!indexName) {
    return { ok: false, reason: 'directory target has no README.md, index.md, or INDEX.md' };
  }

  return { ok: true, path: path.join(inspected.path, indexName) };
}

async function listMarkdownFiles(repositoryRoot) {
  const files = [];
  const readDirectory = async (directoryPath) => {
    const entries = await readdir(directoryPath, { withFileTypes: true });
    entries.sort((left, right) => left.name.localeCompare(right.name, 'en'));
    for (const entry of entries) {
      const entryPath = path.join(directoryPath, entry.name);
      if (entry.isDirectory()) {
        await readDirectory(entryPath);
      } else if (entry.isFile() && entry.name.toLowerCase().endsWith('.md')) {
        files.push(entryPath);
      }
    }
  };

  const readmePath = path.join(repositoryRoot, 'README.md');
  try {
    await lstat(readmePath);
    files.push(readmePath);
  } catch {
    // Repositories using this module as a fixture do not need a root README.
  }

  const docsPath = path.join(repositoryRoot, 'docs');
  try {
    await readDirectory(docsPath);
  } catch {
    // A missing docs directory simply contributes no files to the inventory.
  }

  return files;
}

async function checkMarkdown(repositoryRoot) {
  const absoluteRoot = path.resolve(repositoryRoot);
  const files = await listMarkdownFiles(absoluteRoot);
  const scans = new Map();
  const issues = [];
  const inventory = [];

  for (const filePath of files) {
    const relativePath = normalizeRelative(path.relative(absoluteRoot, filePath));
    const source = await readFile(filePath, 'utf8');
    const scan = scanMarkdown(source, relativePath);
    scans.set(filePath, scan);
    issues.push(...scan.issues);
  }

  for (const filePath of files) {
    const relativePath = normalizeRelative(path.relative(absoluteRoot, filePath));
    const scan = scans.get(filePath);

    for (const link of scan.links) {
      const destination = link.destination.trim();
      if (!destination || isSkippedDestination(destination)) {
        continue;
      }

      const hashIndex = destination.indexOf('#');
      const pathAndQuery = hashIndex === -1 ? destination : destination.slice(0, hashIndex);
      const rawFragment = hashIndex === -1 ? '' : destination.slice(hashIndex + 1);
      const queryIndex = pathAndQuery.indexOf('?');
      const rawPath = queryIndex === -1 ? pathAndQuery : pathAndQuery.slice(0, queryIndex);
      const inventoryEntry = {
        destination,
        file: relativePath,
        line: link.line,
      };
      inventory.push(inventoryEntry);

      let resolved;
      try {
        resolved = rawPath
          ? await resolveTarget(absoluteRoot, filePath, rawPath)
          : { ok: true, path: filePath };
      } catch (error) {
        issues.push({
          ...inventoryEntry,
          type: 'malformed-link',
          detail: `Cannot decode or inspect target: ${error.message}`,
        });
        continue;
      }

      if (!resolved.ok) {
        issues.push({
          ...inventoryEntry,
          type: 'invalid-target',
          detail: resolved.reason,
        });
        continue;
      }

      if (rawFragment && resolved.path.toLowerCase().endsWith('.md')) {
        let fragment;
        try {
          fragment = decodeURIComponent(rawFragment);
        } catch (error) {
          issues.push({
            ...inventoryEntry,
            type: 'malformed-link',
            detail: `Cannot decode fragment: ${error.message}`,
          });
          continue;
        }

        let targetScan = scans.get(resolved.path);
        if (!targetScan) {
          const targetSource = await readFile(resolved.path, 'utf8');
          targetScan = scanMarkdown(
            targetSource,
            normalizeRelative(path.relative(absoluteRoot, resolved.path)),
          );
          scans.set(resolved.path, targetScan);
        }

        if (!buildAnchorSet(targetScan).has(fragment)) {
          issues.push({
            ...inventoryEntry,
            type: 'missing-fragment',
            detail: `Fragment '#${fragment}' does not match a heading or explicit anchor`,
          });
        }
      }
    }
  }

  const byLocation = (left, right) => (
    left.file.localeCompare(right.file, 'en')
    || left.line - right.line
    || left.destination?.localeCompare(right.destination ?? '', 'en')
    || left.type.localeCompare(right.type, 'en')
  );
  inventory.sort(byLocation);
  issues.sort(byLocation);

  return {
    files: files.map((filePath) => normalizeRelative(path.relative(absoluteRoot, filePath))),
    inventory,
    issues,
  };
}

async function main() {
  const scriptPath = fileURLToPath(import.meta.url);
  const repositoryRoot = path.resolve(path.dirname(scriptPath), '..', '..');
  const result = await checkMarkdown(repositoryRoot);

  for (const issue of result.issues) {
    console.error(
      `${issue.file}:${issue.line}: ${issue.type}: ${issue.detail}`
      + (issue.destination ? ` (${issue.destination})` : ''),
    );
  }

  console.log(
    `Checked ${result.files.length} Markdown files and `
    + `${result.inventory.length} relative links; found ${result.issues.length} failure(s).`,
  );
  process.exitCode = result.issues.length === 0 ? 0 : 1;
}

const invokedPath = process.argv[1] ? path.resolve(process.argv[1]) : '';
if (invokedPath === fileURLToPath(import.meta.url)) {
  await main();
}

export {
  buildAnchorSet,
  checkMarkdown,
  extractInlineDestinations,
  githubSlug,
  scanMarkdown,
};
