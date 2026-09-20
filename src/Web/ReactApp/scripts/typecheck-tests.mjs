import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { clampedOverride } from "./typecheck-app-core.mjs";
import { evaluate } from "./typecheck-tests-core.mjs";

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
  ? { error: undefined, signal: null, status: 1, stdout: "", stderr: "" }
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
