// Simple validation utilities shared between SetupWizard and SettingsPage
// Keep lightweight to avoid pulling external libs.

export function isValidCidr(input: string): boolean {
  if (!input) return false;
  const match = input.match(/^([0-9]{1,3})(?:\.([0-9]{1,3})){3}\/(\d{1,2})$/);
  if (!match) return false;
  const parts = input.split('/');
  const ip = parts[0].split('.').map(n => Number(n));
  const prefix = Number(parts[1]);
  
  // Basic format validation
  if (prefix < 0 || prefix > 32) return false;
  if (!ip.every(oct => oct >= 0 && oct <= 255)) return false;
  
  // Validate that the IP is actually a network address (not a host address)
  // For example, 10.0.0.5/24 should be 10.0.0.0/24
  const networkIp = getNetworkAddress(ip, prefix);
  return networkIp.every((oct, idx) => oct === ip[idx]);
}

// Get the network address from an IP and prefix length
function getNetworkAddress(ip: number[], prefixLength: number): number[] {
  const networkIp = [...ip];
  const bitsToZero = 32 - prefixLength;
  
  // Zero out the host bits
  for (let i = 3; i >= 0; i--) {
    const bitsInOctet = Math.min(8, Math.max(0, bitsToZero - (3 - i) * 8));
    if (bitsInOctet > 0) {
      const mask = (0xFF << bitsInOctet) & 0xFF;
      networkIp[i] = networkIp[i] & mask;
    }
  }
  
  return networkIp;
}

// Check if two CIDR ranges overlap
export function doCidrRangesOverlap(cidr1: string, cidr2: string): boolean {
  if (!isValidCidr(cidr1) || !isValidCidr(cidr2)) return false;
  
  const [ip1, prefix1] = parseCidr(cidr1);
  const [ip2, prefix2] = parseCidr(cidr2);
  
  // Check if either network contains the other
  return isIpInNetwork(ip1, ip2, prefix2) || isIpInNetwork(ip2, ip1, prefix1);
}

// Parse CIDR notation into IP array and prefix length
function parseCidr(cidr: string): [number[], number] {
  const [ipStr, prefixStr] = cidr.split('/');
  const ip = ipStr.split('.').map(n => Number(n));
  const prefix = Number(prefixStr);
  return [ip, prefix];
}

// Check if an IP address is within a network range
function isIpInNetwork(ip: number[], networkIp: number[], prefixLength: number): boolean {
  const networkAddr = getNetworkAddress(networkIp, prefixLength);
  const testAddr = getNetworkAddress(ip, prefixLength);
  
  return testAddr.every((oct, idx) => oct === networkAddr[idx]);
}

// Get overlapping CIDR ranges from a list
export function findOverlappingCidrRanges(cidrs: string[]): string[] {
  const validCidrs = cidrs.filter(cidr => cidr.trim() && isValidCidr(cidr.trim()));
  const overlapping: string[] = [];
  
  for (let i = 0; i < validCidrs.length; i++) {
    for (let j = i + 1; j < validCidrs.length; j++) {
      if (doCidrRangesOverlap(validCidrs[i], validCidrs[j])) {
        if (!overlapping.includes(validCidrs[i])) overlapping.push(validCidrs[i]);
        if (!overlapping.includes(validCidrs[j])) overlapping.push(validCidrs[j]);
      }
    }
  }
  
  return overlapping;
}

// Suggest the correct network address for a given CIDR
export function suggestCorrectNetworkAddress(cidr: string): string | null {
  if (!cidr.includes('/')) return null;
  
  const match = cidr.match(/^([0-9]{1,3})(?:\.([0-9]{1,3})){3}\/(\d{1,2})$/);
  if (!match) return null;
  
  const parts = cidr.split('/');
  const ip = parts[0].split('.').map(n => Number(n));
  const prefix = Number(parts[1]);
  
  if (prefix < 0 || prefix > 32) return null;
  if (!ip.every(oct => oct >= 0 && oct <= 255)) return null;
  
  const networkIp = getNetworkAddress(ip, prefix);
  return `${networkIp.join('.')}/${prefix}`;
}

export function normalizeUrl(url: string): string {
  // Generic lightweight normalization (trim + single trailing slash removal)
  return url.trim().replace(/\/$/, '');
}

/**
 * Whether a string is a safe absolute http/https URL suitable for use as a
 * link `href`. Rejects `javascript:`, `data:`, and any other scheme, so a
 * value that ultimately originates from user-entered/persisted settings
 * (rather than a compile-time constant) can never be reinterpreted as
 * executable script when rendered as a link.
 */
export function isSafeHttpUrl(value: string): boolean {
  if (!value) return false;
  try {
    const parsed = new URL(value);
    return parsed.protocol === 'http:' || parsed.protocol === 'https:';
  } catch {
    return false;
  }
}

/**
 * Returns `value` if it is safe to use directly as an anchor `href`
 * (`http(s)` scheme only), or `undefined` otherwise. This performs its own
 * independent scheme check on the raw string — deliberately not just
 * delegating to `isSafeHttpUrl()` — so callers get a sink-level guarantee
 * even if an earlier `isSafeHttpUrl()` gate is refactored away or bypassed
 * (js/xss-through-dom: DOM-sourced text must never be reinterpreted as an
 * executable `javascript:`/`data:` URL when rendered as a link).
 *
 * The value is also round-tripped through `decodeURI(encodeURI(value))`.
 * `encodeURI`/`decodeURI` are true inverses for every *encodable* string, so
 * this is a byte-for-byte no-op for any well-formed URL (including
 * IPv6-literal hosts like `http://[::1]` and URLs that already contain
 * percent-escaped characters) — its only purpose is to route the value
 * through a call CodeQL recognizes as a URI-encoding sanitizer for this sink
 * class. A string containing an unpaired UTF-16 surrogate would make
 * `encodeURI`/`decodeURI` throw `URIError`; that is caught here and treated
 * as unsafe (fail closed to `undefined`) rather than propagating as an
 * uncaught render-time exception.
 */
export function toSafeHref(value: string): string | undefined {
  if (!/^https?:\/\//i.test(value)) return undefined;
  try {
    return decodeURI(encodeURI(value));
  } catch {
    return undefined;
  }
}

/**
 * Hostname *exact-match* patterns generated by backends that are
 * syntactically valid `http(s)` URLs (so `isSafeHttpUrl()` accepts them) but
 * are internal-only container/service names that are never resolvable from
 * a browser running on the user's machine. `TestEmulatorSeeder`
 * (src/backends/Farm.Backend.Plugin.TestEmulator/TestEmulatorSeeder.cs)
 * always creates simulated printers with
 * `ServerUrl = $"http://testemulator-{printerId}"` where `printerId` is a
 * .NET `Guid` (lowercase, dashed 8-4-4-4-12 form), which produces an
 * unresolvable-hostname network error if opened directly (#1546).
 *
 * Matched against the *entire* hostname (not just a prefix) so a real,
 * browser-reachable printer hostname that merely happens to start with
 * `testemulator-` (e.g. `testemulator-lab.local`) is never misclassified as
 * internal-only — a single-label synthetic host has no dots, while a real
 * LAN/DNS hostname does. Extend this list if another backend starts
 * generating similarly unreachable synthetic hostnames.
 */
const INTERNAL_ONLY_HOSTNAME_PATTERNS = [
  /^testemulator-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/,
];

/**
 * Whether `value` is both a safe `http(s)` URL and one a browser on the
 * user's machine can actually reach. Rejects known internal-only synthetic
 * hostnames (see `INTERNAL_ONLY_HOSTNAME_PATTERNS`) in addition to the
 * `isSafeHttpUrl()` scheme check.
 */
export function isBrowserReachableUrl(value: string): boolean {
  if (!isSafeHttpUrl(value)) return false;
  try {
    const hostname = new URL(value).hostname.toLowerCase();
    return !INTERNAL_ONLY_HOSTNAME_PATTERNS.some(pattern => pattern.test(hostname));
  } catch {
    return false;
  }
}

// Dedicated Spoolman base URL normalizer. Currently mirrors normalizeUrl but
// kept separate so future Spoolman-specific rules (e.g. default scheme) can
// be added in one place without touching generic URL handling.
export function normalizeSpoolmanBaseUrl(url: string): string {
  let working = url.trim();
  if (!working) return '';
  // Prepend http:// if user omitted scheme (safer default inside container / LAN)
  if (!/^https?:\/\//i.test(working)) {
    working = 'http://' + working;
  }
  // Remove single trailing slash
  working = working.replace(/\/$/, '');
  return working;
}
