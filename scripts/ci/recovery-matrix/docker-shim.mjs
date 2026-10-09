import { chmodSync, mkdirSync, renameSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';

export function writeDockerShim(runRoot, deploymentRoot, networkAttemptsPath, { realDocker = '/usr/bin/docker' } = {}) {
  const shim = join(runRoot, 'docker');
  const log = join(runRoot, 'docker-commands.ndjson');
  const failureLog = join(runRoot, 'docker-command-failures.ndjson');
  const envFile = join(deploymentRoot, '.env');
  const composeFaultEnable = join(runRoot, 'fault-compose-up.enable');
  const composeFaultPause = join(runRoot, 'fault-compose-up.pause');
  const composeFaultDecision = join(runRoot, 'fault-compose-up.decision');
  mkdirSync(dirname(networkAttemptsPath), { recursive: true });
  writeFileSync(shim, `#!/usr/bin/env bash
set -euo pipefail
args=("$@")
attempts=${JSON.stringify(networkAttemptsPath)}
compose_fault_enable=${JSON.stringify(composeFaultEnable)}
compose_fault_pause=${JSON.stringify(composeFaultPause)}
compose_fault_decision=${JSON.stringify(composeFaultDecision)}
deny() {
  local reason="$1"
  mkdir -p "$(dirname "$attempts")"
  printf '{"at":"%s","protocol":"docker-daemon","source":"docker-shim","destination":%s,"reason":%s}\\n' \
    "$(date -u +%FT%TZ)" \
    "$(node -e 'process.stdout.write(JSON.stringify(process.argv[1]))' "docker:$reason")" \
    "$(node -e 'process.stdout.write(JSON.stringify(process.argv.slice(1).join(" ")))' "$@")" >> "$attempts"
  echo "docker shim denied daemon-mediated network command: $reason" >&2
  exit 75
}
has_pull_never=0
has_pull_other=0
for ((i=0; i<\${#args[@]}; i++)); do
  case "\${args[$i]}" in
    --pull=never) has_pull_never=1 ;;
    --pull)
      if [[ "\${args[$((i+1))]:-}" == "never" ]]; then has_pull_never=1; else has_pull_other=1; fi
      ;;
    --pull=*) has_pull_other=1 ;;
  esac
done
case "\${args[0]:-}" in
  pull|push|login|search|build|buildx|plugin|trust) deny "\${args[0]}" "$@" ;;
  image)
    case "\${args[1]:-}" in pull|push|build) deny "image \${args[1]}" "$@" ;; esac
    ;;
  builder)
    case "\${args[1]:-}" in build|prune) deny "builder \${args[1]}" "$@" ;; esac
    ;;
  run|create)
    if [[ "$has_pull_other" == 1 ]]; then deny "\${args[0]} --pull" "$@"; fi
    if [[ "$has_pull_never" == 0 ]]; then args=("\${args[0]}" "--pull=never" "\${args[@]:1}"); fi
    ;;
  compose)
    has_env_file=0
    for arg in "\${args[@]}"; do
      if [[ "$arg" == "--env-file" ]]; then
        has_env_file=1
        break
      fi
    done
    if [[ "$has_env_file" == 0 ]]; then
      args=("compose" "--env-file" ${JSON.stringify(envFile)} "\${args[@]:1}")
    fi
    for ((i=1; i<\${#args[@]}; i++)); do
      case "\${args[$i]}" in
        pull|build) deny "compose \${args[$i]}" "$@" ;;
        up)
          if [[ "$has_pull_other" == 1 ]]; then deny "compose up --pull" "$@"; fi
          if [[ "$has_pull_never" == 0 ]]; then
            args=("\${args[@]:0:$((i+1))}" "--pull" "never" "\${args[@]:$((i+1))}")
          fi
          if [[ -f "$compose_fault_enable" ]]; then
            fault_mode="$(cat "$compose_fault_enable")"
            rm -f "$compose_fault_enable" "$compose_fault_pause" "$compose_fault_decision"
            printf '{"at":"%s","args":%s}\\n' "$(date -u +%FT%TZ)" "$(node -e 'process.stdout.write(JSON.stringify(process.argv.slice(1)))' "$@")" > "$compose_fault_pause"
            if [[ "$fault_mode" == "fail-now" ]]; then
              echo "recovery matrix injected compose-up activation fault" >&2
              exit 70
            fi
            deadline=$((SECONDS + 300))
            while [[ ! -f "$compose_fault_decision" ]]; do
              if (( SECONDS > deadline )); then
                echo "timed out waiting for recovery matrix compose fault decision" >&2
                exit 70
              fi
              sleep 0.1
            done
            decision="$(cat "$compose_fault_decision")"
            rm -f "$compose_fault_pause" "$compose_fault_decision"
            if [[ "$decision" == "fail" ]]; then
              echo "recovery matrix injected compose-up activation fault" >&2
              exit 70
            fi
          fi
          break
          ;;
      esac
    done
    ;;
esac
log_command() {
  # The leading newline keeps a record torn by a SIGKILL mid-write from swallowing the next one.
  printf '\\n{"at":"%s","args":%s}\\n' "$(date -u +%FT%TZ)" "$(node -e 'process.stdout.write(JSON.stringify(process.argv.slice(1)))' -- "$@")" >> ${JSON.stringify(log)}
}
${pauseGateBash({
    specPath: join(runRoot, dockerFaultFiles.spec),
    pausePath: join(runRoot, dockerFaultFiles.pause),
    decisionPath: join(runRoot, dockerFaultFiles.decision),
  })}
fault_mode="$(fault_gate_claim "$@")"
if [[ "$fault_mode" == "pause-before" ]]; then
  fault_gate_pause "$fault_mode" 0 "$@"
  if [[ "$(fault_gate_decision)" != "run" ]]; then
    echo "recovery matrix injected docker fault before side effect" >&2
    exit 70
  fi
elif [[ "$fault_mode" == "pause-after" ]]; then
  log_command "$@"
  set +e
  ${JSON.stringify(realDocker)} "\${args[@]}"
  fault_status=$?
  set -e
  fault_gate_pause "$fault_mode" "$fault_status" "$@"
  if [[ "$(fault_gate_decision)" == "fail" ]]; then
    echo "recovery matrix injected docker fault after side effect" >&2
    exit 70
  fi
  exit "$fault_status"
fi
log_command "$@"
if [[ "\${args[0]:-}" == "compose" ]]; then
  # The host-update CLI reduces compose failures to an exception name; keep the daemon's own
  # explanation so a failed apply or rollback is diagnosable from the run directory.
  stderr_file="$(mktemp)"
  set +e
  ${JSON.stringify(realDocker)} "\${args[@]}" 2>"$stderr_file"
  rc=$?
  set -e
  cat "$stderr_file" >&2
  if (( rc != 0 )); then
    printf '{"at":"%s","exitCode":%d,"args":%s,"stderr":%s}\\n' "$(date -u +%FT%TZ)" "$rc" \\
      "$(node -e 'process.stdout.write(JSON.stringify(process.argv.slice(1)))' -- "$@")" \\
      "$(tail -c 4000 "$stderr_file" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>process.stdout.write(JSON.stringify(s)))')" \\
      >> ${JSON.stringify(failureLog)}
  fi
  rm -f "$stderr_file"
  exit "$rc"
fi
exec ${JSON.stringify(realDocker)} "\${args[@]}"
`);
  chmodSync(shim, 0o755);
  return shim;
}

// One-shot pause gate shared by the docker shim and tool wrappers. The harness arms it by
// writing {"tokens":[...],"mode":"pause-before"|"pause-after"} to the spec path; the first
// invocation whose argv contains every token claims it atomically, records the pause marker
// and blocks until the harness writes a decision (run | return | fail) or the host is killed.
export function pauseGateBash({ specPath, pausePath, decisionPath }) {
  return `fault_gate_spec=${JSON.stringify(specPath)}
fault_gate_pause_path=${JSON.stringify(pausePath)}
fault_gate_decision_path=${JSON.stringify(decisionPath)}
fault_gate_claim() {
  [[ -f "$fault_gate_spec" ]] || return 0
  local mode
  mode="$(node -e 'const fs=require("fs");let s;try{s=JSON.parse(fs.readFileSync(process.argv[1],"utf8"))}catch{process.exit(0)}const a=process.argv.slice(2);if(Array.isArray(s.tokens)&&s.tokens.every(t=>a.includes(t)))process.stdout.write(String(s.mode))' -- "$fault_gate_spec" "$@" 2>/dev/null || true)"
  [[ -n "$mode" ]] || return 0
  mv "$fault_gate_spec" "$fault_gate_spec.claimed.$$" 2>/dev/null || return 0
  rm -f "$fault_gate_decision_path"
  printf '%s' "$mode"
}
fault_gate_pause() {
  local mode="$1" status="$2"
  shift 2
  printf '{"at":"%s","pid":%s,"mode":"%s","status":%s,"args":%s}\\n' "$(date -u +%FT%TZ)" "$$" "$mode" "$status" \\
    "$(node -e 'process.stdout.write(JSON.stringify(process.argv.slice(1)))' -- "$@")" > "$fault_gate_pause_path.tmp"
  mv "$fault_gate_pause_path.tmp" "$fault_gate_pause_path"
}
fault_gate_decision() {
  local deadline=$((SECONDS + 600))
  while [[ ! -f "$fault_gate_decision_path" ]]; do
    if (( SECONDS > deadline )); then
      printf 'fail'
      return 0
    fi
    sleep 0.1
  done
  cat "$fault_gate_decision_path"
  rm -f "$fault_gate_decision_path"
}`;
}

export const dockerFaultFiles = Object.freeze({
  spec: 'fault-docker.json',
  pause: 'fault-docker.pause',
  decision: 'fault-docker.decision',
});

// Wraps an existing tool shim (for example pg_dump) with the same one-shot pause gate so the
// harness can hold a safe step at a deterministic point, and records every invocation so a
// scenario can prove a restore or dump was (not) replayed. The original shim is kept beside it.
export function wrapToolWithPauseGate(toolPath, { name }) {
  const realPath = `${toolPath}.real`;
  renameSync(toolPath, realPath);
  const dir = dirname(toolPath);
  const callsPath = join(dir, `fault-${name}.calls`);
  writeFileSync(toolPath, `#!/usr/bin/env bash
set -euo pipefail
printf '\\n%s\\n' "$(date -u +%FT%TZ)" >> ${JSON.stringify(callsPath)}
${pauseGateBash({
    specPath: join(dir, `fault-${name}.json`),
    pausePath: join(dir, `fault-${name}.pause`),
    decisionPath: join(dir, `fault-${name}.decision`),
  })}
fault_mode="$(fault_gate_claim "$@")"
if [[ "$fault_mode" == "pause-before" ]]; then
  fault_gate_pause "$fault_mode" 0 "$@"
  if [[ "$(fault_gate_decision)" != "run" ]]; then
    echo "recovery matrix injected ${name} fault before side effect" >&2
    exit 70
  fi
fi
exec ${JSON.stringify(realPath)} "$@"
`);
  chmodSync(toolPath, 0o755);
  return {
    spec: join(dir, `fault-${name}.json`),
    pause: join(dir, `fault-${name}.pause`),
    decision: join(dir, `fault-${name}.decision`),
    calls: callsPath,
  };
}
