const secretEnvKeyPattern = /(password|passwd|secret|api[_-]?key|token|jwt__key)/i;
const credentialArgPattern = /(^|\s)(-P|--password)(\s+|=)("[^"]*"|'[^']*'|\S+)/g;

export const redactedPlaceholder = '[REDACTED]';

export function secretValuesFrom(env = {}) {
  return Object.entries(env)
    .filter(([key, value]) => secretEnvKeyPattern.test(key) && typeof value === 'string' && value.length >= 4)
    .map(([, value]) => value)
    .sort((a, b) => b.length - a.length);
}

export function redactSecrets(text, secrets = []) {
  let result = String(text ?? '');
  for (const secret of secrets) {
    if (secret) {
      result = result.split(secret).join(redactedPlaceholder);
    }
  }
  return result.replace(credentialArgPattern, (_match, lead, flag, sep) => `${lead}${flag}${sep}${redactedPlaceholder}`);
}
