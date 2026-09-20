import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { clampedOverride, evaluate } from "./typecheck-tests-core.mjs";

const projectDirectory = fileURLToPath(new URL("..", import.meta.url));
const tscPath = resolve(projectDirectory, "node_modules/typescript/bin/tsc");
const baselinePath = resolve(
  projectDirectory,
  "scripts/test-typecheck-baseline.json",
);
// Keep diagnostics and file discovery on the identical compiler project.
const tscArguments = [
  "-p",
  "tsconfig.test.json",
  "--noEmit",
  "--pretty",
  "false",
];

const DEFAULT_TIMEOUT_MS = 120_000;
// Node's default maxBuffer is ~1 MiB, which is too small for large typecheck
// output and causes false ENOBUFS failures; the test ratchet intentionally
// raises it to 10 MiB to match the application-side guard rationale.
const DEFAULT_MAX_BUFFER = 10 * 1024 * 1024;

const timeoutMs = clampedOverride(
  process.env.TYPECHECK_TEST_TIMEOUT_MS,
  DEFAULT_TIMEOUT_MS,
);
const maxBuffer = clampedOverride(
  process.env.TYPECHECK_TEST_MAX_BUFFER,
  DEFAULT_MAX_BUFFER,
);
const spawnOptions = {
  cwd: projectDirectory,
  encoding: "utf8",
  timeout: timeoutMs,
  maxBuffer,
};

const baseline = JSON.parse(readFileSync(baselinePath, "utf8"));
const compilerResult = spawnSync(
  process.execPath,
  [tscPath, ...tscArguments],
  spawnOptions,
);
const output = `${compilerResult.stdout ?? ""}${compilerResult.stderr ?? ""}`;
process.stdout.write(output);

const compilerAlreadyFatal =
  Boolean(compilerResult.error) ||
  Boolean(compilerResult.signal) ||
  compilerResult.status === null ||
  (compilerResult.status !== 0 && compilerResult.status !== 2);

const listFilesResult = compilerAlreadyFatal
  // This synthetic object is unreachable by construction: compilerAlreadyFatal
  // is only true when the first compiler spawn already failed fatally, so a
  // future edit that lets it reach here would incorrectly blame the second
  // listFilesOnly pass for a compiler failure. Keeping the synthetic status as
  // null makes that misattribution fail closed to the "did not complete"
  // path rather than the list-files pass.
  ? { error: undefined, signal: null, status: null, stdout: "", stderr: "" }
  : spawnSync(
      process.execPath,
      [tscPath, ...tscArguments, "--listFilesOnly"],
      spawnOptions,
    );
const listFilesOutput = `${listFilesResult.stdout ?? ""}${listFilesResult.stderr ?? ""}`;
const evaluation = evaluate({
  baseline,
  compilerResult,
  listFilesResult,
  output,
  listFilesOutput,
  directory: projectDirectory,
});

if (!evaluation.ok) {
  console.error(evaluation.message);

  if (evaluation.showListFilesOutput) {
    process.stdout.write(listFilesOutput);
  }

  process.exitCode = 1;
} else {
  console.warn(evaluation.message);
}
