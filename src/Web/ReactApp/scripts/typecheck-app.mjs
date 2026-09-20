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

const baseline = JSON.parse(readFileSync(baselinePath, "utf8"));
const compilerResult = spawnSync(process.execPath, [tscPath, ...tscArguments], {
  cwd: projectDirectory,
  encoding: "utf8",
});
const output = `${compilerResult.stdout}${compilerResult.stderr}`;
process.stdout.write(output);

const evaluation = evaluate({ baseline, compilerResult, output });

if (!evaluation.ok) {
  console.error(evaluation.message);
  process.exitCode = 1;
} else {
  console.warn(evaluation.message);
}
