import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { parseApiDateTime, parseApiDateTimeValue, formatDuration, formatPrintTimeMinutes } from '../datetime';

describe('datetime utilities', () => {
  describe('parseApiDateTime', () => {
    it('should parse UTC datetime string with Z suffix', () => {
      const input = '2024-01-15T10:30:00Z';
      const result = parseApiDateTime(input);
      
      expect(result).toBeInstanceOf(Date);
      expect(result.toISOString()).toBe('2024-01-15T10:30:00.000Z');
    });

    it('should parse datetime string with timezone offset', () => {
      const input = '2024-01-15T10:30:00+05:00';
      const result = parseApiDateTime(input);
      
      expect(result).toBeInstanceOf(Date);
    });

    it('should append Z to datetime string without timezone indicator', () => {
      const input = '2024-01-15T10:30:00';
      const result = parseApiDateTime(input);
      
      expect(result.toISOString()).toBe('2024-01-15T10:30:00.000Z');
    });

    it('should handle datetime with negative timezone offset', () => {
      const input = '2024-01-15T10:30:00-05:00';
      const result = parseApiDateTime(input);
      
      expect(result).toBeInstanceOf(Date);
    });
  });

  describe('parseApiDateTimeValue', () => {
    it('should parse string datetime value', () => {
      const input = '2024-01-15T10:30:00';
      const result = parseApiDateTimeValue(input);
      
      expect(result).toBeInstanceOf(Date);
      expect(result.toISOString()).toBe('2024-01-15T10:30:00.000Z');
    });

    it('should return Date object as-is', () => {
      const input = new Date('2024-01-15T10:30:00Z');
      const result = parseApiDateTimeValue(input);
      
      expect(result).toBe(input);
      expect(result).toBeInstanceOf(Date);
    });
  });

  describe('formatDuration', () => {
    beforeEach(() => {
      vi.useFakeTimers();
    });

    afterEach(() => {
      vi.useRealTimers();
    });

    it('should format duration in seconds', () => {
      const start = new Date('2024-01-15T10:00:00Z');
      const end = new Date('2024-01-15T10:00:45Z');
      
      const result = formatDuration(start, end);
      
      expect(result).toBe('45s');
    });

    it('should format duration in minutes and seconds', () => {
      const start = new Date('2024-01-15T10:00:00Z');
      const end = new Date('2024-01-15T10:05:30Z');
      
      const result = formatDuration(start, end);
      
      expect(result).toBe('5m 30s');
    });

    it('should format duration in hours and minutes', () => {
      const start = new Date('2024-01-15T10:00:00Z');
      const end = new Date('2024-01-15T12:30:00Z');
      
      const result = formatDuration(start, end);
      
      expect(result).toBe('2h 30m');
    });

    it('should handle duration with only hours', () => {
      const start = new Date('2024-01-15T10:00:00Z');
      const end = new Date('2024-01-15T13:00:00Z');
      
      const result = formatDuration(start, end);
      
      expect(result).toBe('3h 0m');
    });

    it('should use current time as default end time', () => {
      const now = new Date('2024-01-15T10:10:00Z');
      vi.setSystemTime(now);
      
      const start = new Date('2024-01-15T10:00:00Z');
      
      const result = formatDuration(start);
      
      expect(result).toBe('10m 0s');
    });

    it('should handle string datetime inputs', () => {
      const start = '2024-01-15T10:00:00';
      const end = '2024-01-15T10:02:30';
      
      const result = formatDuration(start, end);
      
      expect(result).toBe('2m 30s');
    });

    it('should return 0s for negative duration', () => {
      const start = new Date('2024-01-15T10:00:00Z');
      const end = new Date('2024-01-15T09:00:00Z');
      
      const result = formatDuration(start, end);
      
      expect(result).toBe('0s');
    });

    it('should handle very long durations', () => {
      const start = new Date('2024-01-15T10:00:00Z');
      const end = new Date('2024-01-16T12:30:00Z');
      
      const result = formatDuration(start, end);
      
      expect(result).toBe('26h 30m');
    });
  });

  describe('formatPrintTimeMinutes', () => {
    it('should format minutes only', () => {
      expect(formatPrintTimeMinutes(45)).toBe('45m');
    });

    it('should format hours and minutes', () => {
      expect(formatPrintTimeMinutes(150)).toBe('2h 30m');
    });

    it('should format days, hours, and minutes', () => {
      expect(formatPrintTimeMinutes(1590)).toBe('1d 2h 30m');
    });

    it('should format days only', () => {
      expect(formatPrintTimeMinutes(1440)).toBe('1d');
    });

    it('should format hours only (no minutes)', () => {
      expect(formatPrintTimeMinutes(120)).toBe('2h');
    });

    it('should handle zero minutes', () => {
      expect(formatPrintTimeMinutes(0)).toBe('0m');
    });

    it('should omit days when zero', () => {
      expect(formatPrintTimeMinutes(90)).toBe('1h 30m');
    });

    it('should omit hours when zero (but has days)', () => {
      expect(formatPrintTimeMinutes(1470)).toBe('1d 30m');
    });

    it('should handle very large durations', () => {
      expect(formatPrintTimeMinutes(4380)).toBe('3d 1h');
    });

    it('should handle fractional minutes by flooring', () => {
      expect(formatPrintTimeMinutes(90.7)).toBe('1h 30m');
    });

    it('should format multiple days correctly', () => {
      expect(formatPrintTimeMinutes(5760)).toBe('4d');
    });

    it('should show only minutes when less than an hour', () => {
      expect(formatPrintTimeMinutes(59)).toBe('59m');
    });

    it('should show all components when all are present', () => {
      expect(formatPrintTimeMinutes(2955)).toBe('2d 1h 15m');
    });
  });
});
