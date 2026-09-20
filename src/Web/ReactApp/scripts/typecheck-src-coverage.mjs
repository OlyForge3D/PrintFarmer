// Production entry point for #2826: asserts every checkable file under src/
// is covered by at least one of tsconfig.app.json / tsconfig.test.json. See
// typecheck-src-coverage-core.mjs for the comparison logic and rationale.
import { spawnSync } from "node:child_process";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { clampedOverride } from "./typecheck-app-core.mjs";
import { evaluate, walkSrcFiles } from "./typecheck-src-coverage-core.mjs";

const projectDirectory = fileURLToPath(new URL("..", import.meta.url));
const tscPath = resolve(projectDirectory, "node_modules/typescript/bin/tsc");

// Same rationale and same clamped-override seam as typecheck-app.mjs /
// typecheck-tests.mjs: read unconditionally in every run, but clampedOverride
// (typecheck-app-core.mjs) guarantees the env var can only ever SHORTEN the
// default, never lengthen or disable it. Both --listFilesOnly spawns here
// are lighter-weight than a full type-check (no diagnostics are requested),
// but a hung/deadlocked tsc is still possible and must not hang this gate --
// and therefore CI -- indefinitely.
const DEFAULT_TIMEOUT_MS = 120_000;
const DEFAULT_MAX_BUFFER = 10 * 1024 * 1024;
const timeoutMs = clampedOverride(
  process.env.TYPECHECK_SRC_COVERAGE_TIMEOUT_MS,
  DEFAULT_TIMEOUT_MS,
);
const maxBuffer = clampedOverride(
  process.env.TYPECHECK_SRC_COVERAGE_MAX_BUFFER,
  DEFAULT_MAX_BUFFER,
);
const spawnOptions = {
  cwd: projectDirectory,
  encoding: "utf8",
  timeout: timeoutMs,
  maxBuffer,
};

function listProjectFiles(tsconfigName) {
  return spawnSync(
    process.execPath,
    [tscPath, "-p", tsconfigName, "--noEmit", "--listFilesOnly"],
    spawnOptions,
  );
}

const appListFilesResult = listProjectFiles("tsconfig.app.json");
const testListFilesResult = listProjectFiles("tsconfig.test.json");
const appListFilesOutput = `${appListFilesResult.stdout ?? ""}${appListFilesResult.stderr ?? ""}`;
const testListFilesOutput = `${testListFilesResult.stdout ?? ""}${testListFilesResult.stderr ?? ""}`;

const walkedFiles = await walkSrcFiles(projectDirectory);

const evaluation = evaluate({
  appListFilesResult,
  appListFilesOutput,
  testListFilesResult,
  testListFilesOutput,
  walkedFiles,
  directory: projectDirectory,
});

if (!evaluation.ok) {
  console.error(evaluation.message);
  process.exitCode = 1;
} else {
  console.warn(evaluation.message);
}
