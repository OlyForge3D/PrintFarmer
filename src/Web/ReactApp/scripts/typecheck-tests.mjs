import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
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

const baseline = JSON.parse(readFileSync(baselinePath, "utf8"));
const compilerResult = spawnSync(process.execPath, [tscPath, ...tscArguments], {
  cwd: projectDirectory,
  encoding: "utf8",
});
const output = `${compilerResult.stdout}${compilerResult.stderr}`;
process.stdout.write(output);

const listFilesResult = spawnSync(
  process.execPath,
  [tscPath, ...tscArguments, "--listFilesOnly"],
  {
    cwd: projectDirectory,
    encoding: "utf8",
  },
);
const listFilesOutput = `${listFilesResult.stdout}${listFilesResult.stderr}`;
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
