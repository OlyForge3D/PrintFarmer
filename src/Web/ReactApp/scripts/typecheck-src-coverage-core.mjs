// #2826: tsconfig.app.json's exclude side and tsconfig.test.json's include
// side are maintained independently and are not provably complementary. A
// file such as src/foo.spec.ts or src/foo.test.mts is excluded from the app
// project (tsconfig.app.json excludes **/*.spec.*) but never picked up by
// the test project (tsconfig.test.json only includes src/test,
// **/__tests__/**, *.test.ts, *.test.tsx, and a short list of .d.ts paths),
// so neither `npm run typecheck:app` nor `npm run typecheck:test` ever
// type-checks it. This module closes that gap by comparing the UNION of
// what tsc itself reports for both projects (via --listFilesOnly, the
// authoritative source of truth -- not a hand-rolled re-implementation of
// each tsconfig's include/exclude globs, which could silently drift from
// the real compiler behavior) against an independent filesystem walk of
// src/. Anything the walk finds that neither project's file list contains is
// reported by name and fails the gate.
//
// Deliberately NOT consolidated with typecheck-app-core.mjs /
// typecheck-tests-core.mjs: #2827 (extracting a shared module between the
// app-side and tests-side scripts) is a separate, currently undecided
// change. This module duplicates the same small "project-relative, not
// under node_modules, not escaping the project directory" guard idiom those
// two modules use (isAppSourceFile / isTestFile) rather than importing a
// private helper from either.
import { readdir, stat } from "node:fs/promises";
import { isAbsolute, relative, resolve } from "node:path";
import { toRealPath } from "./typecheck-tests-core.mjs";

// Every TypeScript-family source extension either project could plausibly
// claim, including the .d.ts / .d.mts / .d.cts declaration-file forms (which
// end in .ts / .mts / .cts respectively, so no separate branch is needed).
// Deliberately excludes plain .js/.jsx: neither tsconfig sets allowJs, so a
// stray .js file under src/ is not "checkable" by either gate today, and
// treating it as such would make this gate fail for a reason neither
// existing gate can address.
const CHECKABLE_EXTENSION_PATTERN = /\.(?:tsx?|mts|cts)$/i;

export function isCheckableSourceFile(path) {
  return CHECKABLE_EXTENSION_PATTERN.test(path);
}

// Restricts a raw path (from --listFilesOnly output or a filesystem walk) to
// a project-relative, forward-slash-normalized form, or returns undefined if
// the path does not belong under src/ at all (escapes the project directory,
// or lives under any node_modules/, including a nested one). Mirrors the
// exact guard shape of isAppSourceFile (typecheck-app-core.mjs) and
// isTestFile (typecheck-tests-core.mjs) on purpose -- see the module comment
// on why this is a deliberate, small duplication rather than a shared import.
function toSrcRelativePath(path, directory) {
  const normalized = relative(directory, resolve(directory, path)).replaceAll(
    "\\",
    "/",
  );
  if (
    isAbsolute(normalized) ||
    normalized.startsWith("../") ||
    normalized.split("/").includes("node_modules") ||
    !normalized.startsWith("src/")
  ) {
    return undefined;
  }
  return normalized;
}

function isProjectRelativePath(path, directory) {
  const normalized = relative(directory, resolve(directory, path)).replaceAll(
    "\\",
    "/",
  );
  return (
    !isAbsolute(normalized) &&
    !normalized.startsWith("../") &&
    !normalized.split("/").includes("node_modules")
  );
}

// Parses one project's --listFilesOnly output into a Map keyed by realpath
// (so a symlink pointing at an already-listed file cannot be double-counted,
// or -- more importantly here -- cannot make a file LOOK uncovered under one
// spelling while it is actually covered under another), valued by the
// project-relative display path used in failure messages.
export function parseListFilesOutput(listFilesOutput, directory) {
  const files = new Map();

  for (const rawPath of listFilesOutput.split(/\r?\n/)) {
    if (!rawPath) {
      continue;
    }

    const relativePath = toSrcRelativePath(rawPath, directory);
    if (!relativePath || !isCheckableSourceFile(relativePath)) {
      continue;
    }

    const real = toRealPath(resolve(directory, rawPath));
    if (!files.has(real)) {
      files.set(real, relativePath);
    }
  }

  return files;
}

// Recursively walks src/ under `directory`, independent of and not trusting
// either tsconfig, to find every checkable source file that exists on disk.
// This is the "ground truth" side of the diff: whatever this finds that
// neither project's --listFilesOnly output also finds is, by definition, a
// file no type-check gate has ever looked at.
export async function walkSrcFiles(directory) {
  const srcDirectory = resolve(directory, "src");
  const realProjectDirectory = toRealPath(directory);
  const files = new Map();
  const visitedDirectories = new Set();

  async function walkDirectory(currentDirectory) {
    const realDirectory = toRealPath(currentDirectory);
    if (
      visitedDirectories.has(realDirectory) ||
      !isProjectRelativePath(realDirectory, realProjectDirectory)
    ) {
      return;
    }
    visitedDirectories.add(realDirectory);

    let entries;
    try {
      entries = await readdir(currentDirectory, { withFileTypes: true });
    } catch (error) {
      // No src/ directory at all is not this gate's problem to diagnose --
      // every other build/test step already fails loudly in that case. Treat
      // it as "found nothing" rather than crashing this gate with an
      // unrelated, confusing stack trace.
      if (error && error.code === "ENOENT") {
        return;
      }
      throw error;
    }

    for (const entry of entries) {
      const absolutePath = resolve(currentDirectory, entry.name);
      const relativePath = toSrcRelativePath(absolutePath, directory);
      if (!relativePath) {
        continue;
      }

      if (entry.isDirectory()) {
        await walkDirectory(absolutePath);
        continue;
      }

      if (entry.isFile()) {
        if (!isCheckableSourceFile(relativePath)) {
          continue;
        }

        const real = toRealPath(absolutePath);
        if (
          !files.has(real) &&
          isProjectRelativePath(real, realProjectDirectory)
        ) {
          files.set(real, relativePath);
        }
        continue;
      }

      if (!entry.isSymbolicLink()) {
        continue;
      }

      let target;
      try {
        target = await stat(absolutePath);
      } catch (error) {
        if (
          error &&
          (error.code === "ENOENT" || error.code === "ELOOP")
        ) {
          continue;
        }
        throw error;
      }

      if (target.isDirectory()) {
        await walkDirectory(absolutePath);
        continue;
      }

      if (!target.isFile() || !isCheckableSourceFile(relativePath)) {
        continue;
      }

      const real = toRealPath(absolutePath);
      if (
        !files.has(real) &&
        isProjectRelativePath(real, realProjectDirectory)
      ) {
        files.set(real, relativePath);
      }
    }
  }

  await walkDirectory(srcDirectory);

  return files;
}

function describeListFilesFailure(label, result) {
  if (result.error || result.signal || result.status !== 0) {
    return `TypeScript ${label} compiler could not list its project files.`;
  }
  return undefined;
}

export function evaluate({
  appListFilesResult,
  appListFilesOutput,
  testListFilesResult,
  testListFilesOutput,
  walkedFiles,
  directory,
}) {
  // Fail closed independently for each project's listing: an error, a
  // signal death, a timeout (surfaced as `error`), or any non-zero status
  // means that project's file list cannot be trusted, so the diff below
  // must not run against a partial or missing list -- that could silently
  // under-report (or entirely hide) real coverage gaps. Both are checked
  // and reported together (rather than returning on the first) so an edit
  // that breaks both spawns in the same run reports as one failure, not two
  // sequential, seemingly-unrelated ones -- the same lesson #2811 item 1
  // applied to the test ratchet's own two guards.
  const spawnFailures = [
    describeListFilesFailure("application", appListFilesResult),
    describeListFilesFailure("test", testListFilesResult),
  ].filter(Boolean);
  if (spawnFailures.length > 0) {
    return { ok: false, message: spawnFailures.join("\n") };
  }

  const appFiles = parseListFilesOutput(appListFilesOutput, directory);
  const testFiles = parseListFilesOutput(testListFilesOutput, directory);
  const covered = new Set([...appFiles.keys(), ...testFiles.keys()]);

  const uncovered = [...walkedFiles.entries()]
    .filter(([real]) => !covered.has(real))
    .map(([, display]) => display)
    .sort();

  if (uncovered.length > 0) {
    return {
      ok: false,
      message:
        `${uncovered.length} file(s) under src/ match neither ` +
        `tsconfig.app.json nor tsconfig.test.json, so no type-check gate ` +
        `ever checks them. Add each to one project's include/exclude ` +
        `rules:\n  ${uncovered.join("\n  ")}`,
    };
  }

  return {
    ok: true,
    message:
      `Type-check project coverage passed: all ${walkedFiles.size} ` +
      `checkable file(s) under src/ are covered by tsconfig.app.json ` +
      `(${appFiles.size}) and/or tsconfig.test.json (${testFiles.size}).`,
  };
}
