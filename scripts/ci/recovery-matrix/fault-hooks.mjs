import { execFileSync } from 'node:child_process';

export const faultHookPoints = Object.freeze(['before-activate', 'during-activate', 'before-recover']);

export function parseFaultHooks(values = []) {
  const hooks = {};
  for (const value of values) {
    const match = /^(before-activate|during-activate|before-recover)(?:=|:)(.+)$/.exec(value);
    if (!match) {
      throw new Error(`invalid_fault_hook:${value}`);
    }
    hooks[match[1]] = match[2];
  }
  return hooks;
}

export function hasFaultHooks(hooks) {
  return Object.keys(hooks ?? {}).length > 0;
}

export function invokeFaultHook({ hooks, point, context = {}, run = defaultRun }) {
  const command = hooks?.[point];
  if (!command) {
    return false;
  }
  run(command, {
    env: Object.fromEntries(
      Object.entries(context)
        .filter(([, value]) => value !== undefined && value !== null)
        .map(([key, value]) => [`PF_RECOVERY_${key.toUpperCase()}`, String(value)]),
    ),
  });
  return true;
}

function defaultRun(command, { env = {} } = {}) {
  execFileSync('bash', ['-lc', command], {
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
    env: { ...process.env, ...env },
  });
}
