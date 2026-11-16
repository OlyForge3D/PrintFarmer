/** Only explicit axis telemetry is homing evidence; positions and command acceptance are not. */
export function knownHomedAxes(value: string | null | undefined): string | undefined {
  return typeof value === 'string' && /^[xyz]*$/i.test(value) ? value.toLowerCase() : undefined;
}
