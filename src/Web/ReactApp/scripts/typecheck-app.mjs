import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { evaluate } from "./typecheck-app-core.mjs";

const projectDirectory = fileURLToPath(new URL("..", import.meta.url));
const tscPath = resolve(projectDirectory, "node_modules/typescript/bin/tsc");
const baselinePath = resolve(
  projectDirectory,
  "scripts/app-typecheck-baseline.json",
);
const tscArguments = [
  "-p",
  "tsconfig.app.json",
  "--noEmit",
  "--pretty",
  "false",
];

// Overridable only for the CLI-level fixture tests below (fake, hung tsc
// stubs that must be killed quickly instead of the test waiting out the real
// production timeout); production always uses the defaults. Without a bound,
// a tsc deadlock or pathological type recursion would hang the compiler
// indefinitely -- in CI, in Docker image builds, and on every developer
// machine -- with no signal beyond "the job never finishes."
const timeoutMs = Number(process.env.TYPECHECK_APP_TIMEOUT_MS) || 120_000;
const maxBuffer = Number(process.env.TYPECHECK_APP_MAX_BUFFER) || 10 * 1024 * 1024;
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

// A second, identical-project pass just to enumerate files, mirroring
// scripts/typecheck-tests.mjs: needed to scan for @ts-nocheck opt-outs
// (#2811 item 5 / R2) without conflating file discovery with diagnostics.
const listFilesResult = spawnSync(
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
  process.exitCode = 1;
} else {
  console.warn(evaluation.message);
}
