import { describe, it, expect } from 'vitest';
import {
  formatFileSize,
  validateSTLFileSize,
} from './stlFileUtils';

describe('stlFileUtils', () => {
  describe('formatFileSize', () => {
    it('formats bytes correctly', () => {
      expect(formatFileSize(0)).toBe('0 Bytes');
      expect(formatFileSize(1024)).toBe('1 KB');
      expect(formatFileSize(1024 * 1024)).toBe('1 MB');
      expect(formatFileSize(1024 * 1024 * 1024)).toBe('1 GB');
    });

    it('handles decimal sizes', () => {
      // Values under 1024 stay in Bytes
      const result512 = formatFileSize(512);
      expect(result512).toBe('512 Bytes');
      
      // 1536 bytes is greater than 1024 so converts to KB (1.5 KB)
      const result1536 = formatFileSize(1536);
      expect(result1536).toBe('1.5 KB');
      
      // 2048 bytes becomes 2 KB
      const result2048 = formatFileSize(2048);
      expect(result2048).toBe('2 KB');
    });

    it('rounds appropriately', () => {
      const result1536 = formatFileSize(1536);
      expect(result1536).toBe('1.5 KB');
      
      const result2560 = formatFileSize(2560);
      expect(result2560).toBe('2.5 KB');
    });

    it('handles large numbers', () => {
      const result = formatFileSize(1024 * 1024 * 1024 * 5);
      expect(result).toContain('GB');
    });
  });

  describe('validateSTLFileSize', () => {
    it('validates file within size limit', () => {
      const file = new File(['x'.repeat(1024)], 'model.stl', { type: 'application/octet-stream' });
      const result = validateSTLFileSize(file, 2);
      expect(result.valid).toBe(true);
    });

    it('rejects file exceeding size limit', () => {
      const file = new File(['x'.repeat(1024 * 1024 * 10)], 'model.stl', {
        type: 'application/octet-stream',
      });
      const result = validateSTLFileSize(file, 5);
      expect(result.valid).toBe(false);
      expect(result.error).toBeDefined();
    });

    it('handles default max size (50 MB)', () => {
      const file = new File(['x'.repeat(1024)], 'model.stl', { type: 'application/octet-stream' });
      const result = validateSTLFileSize(file);
      expect(result.valid).toBe(true);
    });

    it('handles exact size limit', () => {
      const file = new File(['x'.repeat(1024 * 1024 * 2)], 'model.stl', {
        type: 'application/octet-stream',
      });
      const result = validateSTLFileSize(file, 2);
      expect(result.valid).toBe(true);
    });

    it('handles empty files', () => {
      const file = new File([], 'model.stl', { type: 'application/octet-stream' });
      const result = validateSTLFileSize(file, 50);
      expect(result.valid).toBe(true);
    });
  });

  describe('File size validation edge cases', () => {
    it('handles very small files', () => {
      const file = new File(['a'], 'model.stl', { type: 'application/octet-stream' });
      const result = validateSTLFileSize(file, 50);
      expect(result.valid).toBe(true);
    });

    it('handles boundary conditions', () => {
      // Just under 1 MB
      const file1 = new File(['x'.repeat(1024 * 1024 - 1)], 'model.stl', {
        type: 'application/octet-stream',
      });
      const result1 = validateSTLFileSize(file1, 1);
      expect(result1.valid).toBe(true);

      // Just over 1 MB
      const file2 = new File(['x'.repeat(1024 * 1024 + 1)], 'model.stl', {
        type: 'application/octet-stream',
      });
      const result2 = validateSTLFileSize(file2, 1);
      expect(result2.valid).toBe(false);
    });

    it('handles zero max size', () => {
      const file = new File(['x'.repeat(100)], 'model.stl', {
        type: 'application/octet-stream',
      });
      const result = validateSTLFileSize(file, 0);
      expect(result.valid).toBe(false);
    });
  });
});
