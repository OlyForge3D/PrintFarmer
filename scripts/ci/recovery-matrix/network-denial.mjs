export const canaryDnsName = 'canary.printfarmer.invalid';

export function hasCanaryAttempt(attempts, name = canaryDnsName) {
  return attempts.some((attempt) => attempt.destination === name || attempt.query === name);
}

export function withoutCanaryAttempts(attempts, name = canaryDnsName) {
  return attempts.filter((attempt) => attempt.destination !== name && attempt.query !== name);
}
