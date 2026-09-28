import { chmodSync, mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';

export function writeDockerShim(runRoot, deploymentRoot, networkAttemptsPath) {
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
printf '{"at":"%s","args":%s}\\n' "$(date -u +%FT%TZ)" "$(node -e 'process.stdout.write(JSON.stringify(process.argv.slice(1)))' "$@")" >> ${JSON.stringify(log)}
if [[ "\${args[0]:-}" == "compose" ]]; then
  # The host-update CLI reduces compose failures to an exception name; keep the daemon's own
  # explanation so a failed apply or rollback is diagnosable from the run directory.
  stderr_file="$(mktemp)"
  set +e
  /usr/bin/docker "\${args[@]}" 2>"$stderr_file"
  rc=$?
  set -e
  cat "$stderr_file" >&2
  if (( rc != 0 )); then
    printf '{"at":"%s","exitCode":%d,"args":%s,"stderr":%s}\\n' "$(date -u +%FT%TZ)" "$rc" \\
      "$(node -e 'process.stdout.write(JSON.stringify(process.argv.slice(1)))' "$@")" \\
      "$(tail -c 4000 "$stderr_file" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>process.stdout.write(JSON.stringify(s)))')" \\
      >> ${JSON.stringify(failureLog)}
  fi
  rm -f "$stderr_file"
  exit "$rc"
fi
exec /usr/bin/docker "\${args[@]}"
`);
  chmodSync(shim, 0o755);
  return shim;
}
