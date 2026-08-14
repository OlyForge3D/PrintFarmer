import { describe, it, expect } from 'vitest';
import {
  isValidCidr,
  doCidrRangesOverlap,
  findOverlappingCidrRanges,
  suggestCorrectNetworkAddress,
  normalizeUrl,
  normalizeSpoolmanBaseUrl,
  isSafeHttpUrl,
  isBrowserReachableUrl,
  toSafeHref,
} from '../validation';

describe('validation utils', () => {
  describe('isValidCidr', () => {
    it('should validate correct CIDR notation', () => {
      expect(isValidCidr('192.168.1.0/24')).toBe(true);
      expect(isValidCidr('10.0.0.0/8')).toBe(true);
      expect(isValidCidr('172.16.0.0/12')).toBe(true);
    });

    it('should reject invalid CIDR notation', () => {
      expect(isValidCidr('192.168.1.5/24')).toBe(false); // Not a network address
      expect(isValidCidr('192.168.1.0/33')).toBe(false); // Invalid prefix
      expect(isValidCidr('256.1.1.0/24')).toBe(false); // Invalid octet
      expect(isValidCidr('192.168.1')).toBe(false); // Missing prefix
      expect(isValidCidr('')).toBe(false); // Empty string
    });

    it('should validate prefix ranges', () => {
      expect(isValidCidr('0.0.0.0/0')).toBe(true);
      expect(isValidCidr('192.168.1.128/32')).toBe(true);
      expect(isValidCidr('192.168.1.0/-1')).toBe(false);
      expect(isValidCidr('192.168.1.0/33')).toBe(false);
    });
  });

  describe('doCidrRangesOverlap', () => {
    it('should detect overlapping ranges', () => {
      expect(doCidrRangesOverlap('192.168.0.0/16', '192.168.1.0/24')).toBe(true);
      expect(doCidrRangesOverlap('10.0.0.0/8', '10.1.0.0/16')).toBe(true);
    });

    it('should detect non-overlapping ranges', () => {
      expect(doCidrRangesOverlap('192.168.0.0/24', '192.168.1.0/24')).toBe(false);
      expect(doCidrRangesOverlap('10.0.0.0/8', '172.16.0.0/12')).toBe(false);
    });

    it('should return false for invalid CIDR', () => {
      expect(doCidrRangesOverlap('invalid', '192.168.1.0/24')).toBe(false);
      expect(doCidrRangesOverlap('192.168.1.0/24', 'invalid')).toBe(false);
    });
  });

  describe('findOverlappingCidrRanges', () => {
    it('should find overlapping ranges', () => {
      const cidrs = ['192.168.0.0/16', '192.168.1.0/24', '10.0.0.0/8'];
      const overlapping = findOverlappingCidrRanges(cidrs);
      
      expect(overlapping).toContain('192.168.0.0/16');
      expect(overlapping).toContain('192.168.1.0/24');
      expect(overlapping).not.toContain('10.0.0.0/8');
    });

    it('should return empty array when no overlaps', () => {
      const cidrs = ['192.168.0.0/24', '192.168.1.0/24', '192.168.2.0/24'];
      const overlapping = findOverlappingCidrRanges(cidrs);
      
      expect(overlapping).toEqual([]);
    });

    it('should filter out invalid CIDR entries', () => {
      const cidrs = ['192.168.0.0/24', 'invalid', '192.168.1.0/24'];
      const overlapping = findOverlappingCidrRanges(cidrs);
      
      expect(overlapping).toEqual([]);
    });
  });

  describe('suggestCorrectNetworkAddress', () => {
    it('should suggest correct network address', () => {
      expect(suggestCorrectNetworkAddress('192.168.1.5/24')).toBe('192.168.1.0/24');
      expect(suggestCorrectNetworkAddress('10.5.3.7/8')).toBe('10.0.0.0/8');
    });

    it('should return null for invalid input', () => {
      expect(suggestCorrectNetworkAddress('invalid')).toBe(null);
      expect(suggestCorrectNetworkAddress('192.168.1.0')).toBe(null);
      expect(suggestCorrectNetworkAddress('')).toBe(null);
    });

    it('should return same address if already correct', () => {
      expect(suggestCorrectNetworkAddress('192.168.1.0/24')).toBe('192.168.1.0/24');
      expect(suggestCorrectNetworkAddress('10.0.0.0/8')).toBe('10.0.0.0/8');
    });
  });

  describe('normalizeUrl', () => {
    it('should trim and remove trailing slash', () => {
      expect(normalizeUrl('  http://example.com/  ')).toBe('http://example.com');
      expect(normalizeUrl('http://example.com/')).toBe('http://example.com');
      expect(normalizeUrl('http://example.com')).toBe('http://example.com');
    });

    it('should handle empty strings', () => {
      expect(normalizeUrl('')).toBe('');
      expect(normalizeUrl('  ')).toBe('');
    });
  });

  describe('normalizeSpoolmanBaseUrl', () => {
    it('should prepend http:// if missing scheme', () => {
      expect(normalizeSpoolmanBaseUrl('localhost:8080')).toBe('http://localhost:8080');
      expect(normalizeSpoolmanBaseUrl('192.168.1.100')).toBe('http://192.168.1.100');
    });

    it('should preserve existing scheme', () => {
      expect(normalizeSpoolmanBaseUrl('http://example.com')).toBe('http://example.com');
      expect(normalizeSpoolmanBaseUrl('https://example.com')).toBe('https://example.com');
    });

    it('should remove trailing slash', () => {
      expect(normalizeSpoolmanBaseUrl('http://example.com/')).toBe('http://example.com');
      expect(normalizeSpoolmanBaseUrl('localhost:8080/')).toBe('http://localhost:8080');
    });

    it('should handle empty strings', () => {
      expect(normalizeSpoolmanBaseUrl('')).toBe('');
      expect(normalizeSpoolmanBaseUrl('  ')).toBe('');
    });
  });

  describe('isSafeHttpUrl', () => {
    it('accepts absolute http/https URLs', () => {
      expect(isSafeHttpUrl('http://spoolman.local:7912')).toBe(true);
      expect(isSafeHttpUrl('https://spoolman.example.com')).toBe(true);
    });

    it('rejects non-http(s) schemes', () => {
      // Regression for js/xss-through-dom: a persisted/settings-derived value
      // used as an anchor `href` must never resolve to a scheme other than
      // http/https, or clicking the link could execute script instead of
      // navigating. These use inert placeholder bodies, not real payloads.
      expect(isSafeHttpUrl('javascript:void(0)')).toBe(false);
      expect(isSafeHttpUrl('data:text/plain,placeholder')).toBe(false);
      expect(isSafeHttpUrl('file:///etc/passwd')).toBe(false);
    });

    it('rejects malformed or empty input', () => {
      expect(isSafeHttpUrl('')).toBe(false);
      expect(isSafeHttpUrl('not a url')).toBe(false);
    });
  });

  describe('isBrowserReachableUrl', () => {
    // Regression for #1546: TestEmulatorSeeder always creates simulated
    // printers with ServerUrl `http://testemulator-<guid>` — a syntactically
    // valid http(s) URL that isSafeHttpUrl() alone would accept, but whose
    // hostname is an internal Docker service name unresolvable from a
    // browser on the user's machine.
    it('rejects the TestEmulator internal-only hostname', () => {
      expect(isBrowserReachableUrl('http://testemulator-11111111-1111-1111-1111-111111111111')).toBe(false);
      expect(isBrowserReachableUrl('http://TestEmulator-ABCDEFAB-1234-1234-1234-123456789012')).toBe(false);
    });

    it('accepts real printer http/https URLs', () => {
      expect(isBrowserReachableUrl('http://printer-1.local')).toBe(true);
      expect(isBrowserReachableUrl('https://192.168.1.100:7125')).toBe(true);
    });

    it('does not misclassify a real hostname that merely starts with "testemulator-"', () => {
      // Matching must be exact (the full single-label GUID hostname), not a
      // prefix check, so a real LAN/DNS name is never wrongly disabled.
      expect(isBrowserReachableUrl('http://testemulator-lab.local')).toBe(true);
      expect(isBrowserReachableUrl('https://testemulator-prod.example.com')).toBe(true);
    });

    it('rejects unsafe schemes just like isSafeHttpUrl', () => {
      expect(isBrowserReachableUrl('javascript:alert(1)')).toBe(false);
      expect(isBrowserReachableUrl('')).toBe(false);
      expect(isBrowserReachableUrl('not a url')).toBe(false);
    });
  });

  describe('toSafeHref', () => {
    // Regression for js/xss-through-dom: this is the sink-level guard used
    // directly at the `href={...}` assignment in SpoolsTab.tsx. Unlike
    // isSafeHttpUrl() (a boolean gate evaluated earlier in the component),
    // this is exercised here independent of any surrounding component logic.
    it('returns the value unchanged for absolute http/https URLs', () => {
      expect(toSafeHref('http://spoolman.local:7912')).toBe('http://spoolman.local:7912');
      expect(toSafeHref('https://spoolman.example.com')).toBe('https://spoolman.example.com');
    });

    it('returns undefined for non-http(s) schemes', () => {
      // Inert placeholder bodies, not real payloads.
      expect(toSafeHref('javascript:void(0)')).toBeUndefined();
      expect(toSafeHref('data:text/plain,placeholder')).toBeUndefined();
      expect(toSafeHref('file:///etc/passwd')).toBeUndefined();
      expect(toSafeHref('vbscript:msgbox(1)')).toBeUndefined();
    });

    it('returns undefined for protocol-relative or scheme-less input', () => {
      expect(toSafeHref('//evil.example.com')).toBeUndefined();
      expect(toSafeHref('evil.example.com')).toBeUndefined();
    });

    it('returns undefined for malformed or empty input', () => {
      expect(toSafeHref('')).toBeUndefined();
      expect(toSafeHref('not a url')).toBeUndefined();
    });

    it('preserves IPv6-literal hosts and pre-encoded characters unmangled', () => {
      // encodeURI() alone would corrupt these (percent-encode `[`/`]`, or
      // double-encode an existing `%`); the decodeURI(encodeURI(...))
      // round-trip inside toSafeHref() must return them byte-for-byte.
      expect(toSafeHref('http://[::1]:7912')).toBe('http://[::1]:7912');
      expect(toSafeHref('http://host/%20already-encoded')).toBe('http://host/%20already-encoded');
    });

    it('fails closed (returns undefined) for a value containing an unpaired UTF-16 surrogate', () => {
      // encodeURI()/decodeURI() throw URIError on unencodable input; this
      // must be caught and treated as unsafe rather than propagating as an
      // uncaught exception at the render-time sink.
      expect(toSafeHref('http://host/\uD800')).toBeUndefined();
    });
  });
});
