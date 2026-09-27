import { chmodSync, mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';

export function writeDockerShim(runRoot, deploymentRoot, networkAttemptsPath) {
  const shim = join(runRoot, 'docker');
  const log = join(runRoot, 'docker-commands.ndjson');
  const envFile = join(deploymentRoot, '.env');
  mkdirSync(dirname(networkAttemptsPath), { recursive: true });
  writeFileSync(shim, `#!/usr/bin/env bash
set -euo pipefail
args=("$@")
attempts=${JSON.stringify(networkAttemptsPath)}
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
          break
          ;;
      esac
    done
    ;;
esac
printf '{"at":"%s","args":%s}\\n' "$(date -u +%FT%TZ)" "$(node -e 'process.stdout.write(JSON.stringify(process.argv.slice(1)))' "$@")" >> ${JSON.stringify(log)}
exec /usr/bin/docker "\${args[@]}"
`);
  chmodSync(shim, 0o755);
  return shim;
}
