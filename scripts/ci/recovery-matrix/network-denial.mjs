export const canaryDnsName = 'canary.printfarmer.invalid';
export const canaryDnsLookupCommand = `getent hosts ${canaryDnsName}. >/dev/null 2>&1`;

export function hasCanaryAttempt(attempts, name = canaryDnsName) {
  return attempts.some((attempt) => attempt.destination === name || attempt.query === name);
}

export function withoutCanaryAttempts(attempts, name = canaryDnsName) {
  return attempts.filter((attempt) => attempt.destination !== name && attempt.query !== name);
}
