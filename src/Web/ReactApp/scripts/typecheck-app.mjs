import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { clampedOverride, evaluate } from "./typecheck-app-core.mjs";

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

const DEFAULT_TIMEOUT_MS = 120_000;
const DEFAULT_MAX_BUFFER = 10 * 1024 * 1024;

// The env vars below are read unconditionally, in every run -- production,
// CI, and Docker alike. clampedOverride (typecheck-app-core.mjs) ensures
// each can only ever SHORTEN its default, never lengthen or disable it, so
// this is a test-only seam (the CLI-level fixture tests below use it for a
// fake, hung tsc stub killed quickly rather than waiting out the real
// 120s/10MB defaults), not a way to loosen production behavior. Without any
// bound at all, a tsc deadlock or pathological type recursion would hang the
// compiler indefinitely -- in CI, in Docker image builds, and on every
// developer machine -- with no signal beyond "the job never finishes."
const timeoutMs = clampedOverride(
  process.env.TYPECHECK_APP_TIMEOUT_MS,
  DEFAULT_TIMEOUT_MS,
);
const maxBuffer = clampedOverride(
  process.env.TYPECHECK_APP_MAX_BUFFER,
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

// Matches the compiler-result guards evaluate() checks first, in the same
// order: a fatal outcome here (timeout, crash/signal, null status, or a
// nonzero status this invocation cannot legitimately produce) already
// decides the gate, so running a second, identical-project spawnSync call
// just to enumerate files would only double the wait on an already-fatal
// deadlock/hang (e.g. 2x timeoutMs instead of 1x) for no benefit -- evaluate
// never reaches listFilesResult in that case. Skip the second spawn and pass
// a synthetic, already-failed listFilesResult through instead.
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
  process.exitCode = 1;
} else {
  console.warn(evaluation.message);
}
