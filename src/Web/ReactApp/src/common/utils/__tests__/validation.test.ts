import { describe, it, expect } from 'vitest';
import {
  isValidCidr,
  doCidrRangesOverlap,
  findOverlappingCidrRanges,
  suggestCorrectNetworkAddress,
  normalizeUrl,
  normalizeSpoolmanBaseUrl,
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
});
