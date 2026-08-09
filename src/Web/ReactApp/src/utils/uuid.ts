/**
 * Generate a UUID, with fallback for browsers that don't support crypto.randomUUID
 */
export function generateUUID(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  // Fallback for older browsers or non-secure contexts. Still sourced from
  // crypto.getRandomValues() (a CSPRNG) rather than Math.random(), so IDs
  // generated here remain safe to use in security-sensitive contexts (e.g.
  // log/session correlation IDs).
  if (typeof crypto !== 'undefined' && typeof crypto.getRandomValues === 'function') {
    const bytes = crypto.getRandomValues(new Uint8Array(16));
    // Per RFC 4122: set version (4) and variant (10) bits.
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
    return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
  }
  // No CSPRNG available at all (extremely old/non-browser environment).
  // Fail loudly rather than silently degrading to an insecure PRNG.
  throw new Error('generateUUID: no cryptographically secure random source available');
}
