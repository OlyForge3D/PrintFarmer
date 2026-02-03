import { describe, it, expect } from 'vitest';
import { getHarvestErrorInfo, getPhaseDisplay } from '../harvestErrorHelper';
import { GcodeHarvestOperation } from '@/types/api';

describe('harvestErrorHelper', () => {
  describe('getHarvestErrorInfo', () => {
    it('should return null when no error exists', () => {
      const operation: GcodeHarvestOperation = {
        id: 'op-1',
        status: 'Completed',
      } as GcodeHarvestOperation;

      const result = getHarvestErrorInfo(operation);

      expect(result).toBeNull();
    });

    it('should handle ConnectionError', () => {
      const operation: GcodeHarvestOperation = {
        id: 'op-2',
        status: 'Failed',
        error: 'Failed to connect to printer',
        errorType: 'ConnectionError',
        errorPhase: 'Discovery',
        isRetryable: true,
      } as GcodeHarvestOperation;

      const result = getHarvestErrorInfo(operation);

      expect(result).not.toBeNull();
      expect(result?.title).toBe('Connection Failed');
      expect(result?.message).toBe('Failed to connect to printer');
      expect(result?.iconType).toBe('connection');
      expect(result?.canRetry).toBe(true);
      expect(result?.phase).toBe('Discovery');
      expect(result?.suggestion).toContain('network connection');
    });

    it('should handle AuthenticationError', () => {
      const operation: GcodeHarvestOperation = {
        id: 'op-3',
        status: 'Failed',
        error: 'Invalid API key',
        errorType: 'AuthenticationError',
        isRetryable: false,
      } as GcodeHarvestOperation;

      const result = getHarvestErrorInfo(operation);

      expect(result?.title).toBe('Authentication Failed');
      expect(result?.message).toBe('Invalid API key');
      expect(result?.iconType).toBe('auth');
      expect(result?.canRetry).toBe(false);
      expect(result?.suggestion).toContain('API key');
    });

    it('should handle FileSystemError', () => {
      const operation: GcodeHarvestOperation = {
        id: 'op-4',
        status: 'Failed',
        error: 'File not found',
        errorType: 'FileSystemError',
        failedResource: '/path/to/file.gcode',
        isRetryable: true,
      } as GcodeHarvestOperation;

      const result = getHarvestErrorInfo(operation);

      expect(result?.title).toBe('File System Error');
      expect(result?.message).toBe('File not found');
      expect(result?.iconType).toBe('filesystem');
      expect(result?.canRetry).toBe(true);
      expect(result?.failedResource).toBe('/path/to/file.gcode');
      expect(result?.suggestion).toContain('files and folders');
    });

    it('should handle ValidationError', () => {
      const operation: GcodeHarvestOperation = {
        id: 'op-5',
        status: 'Failed',
        error: 'Invalid harvest settings',
        errorType: 'ValidationError',
        isRetryable: false,
      } as GcodeHarvestOperation;

      const result = getHarvestErrorInfo(operation);

      expect(result?.title).toBe('Validation Failed');
      expect(result?.message).toBe('Invalid harvest settings');
      expect(result?.iconType).toBe('validation');
      expect(result?.canRetry).toBe(false);
      expect(result?.suggestion).toContain('settings');
    });

    it('should handle unknown error types', () => {
      const operation: GcodeHarvestOperation = {
        id: 'op-6',
        status: 'Failed',
        error: 'Something went wrong',
        errorType: 'UnknownError',
        isRetryable: false,
      } as GcodeHarvestOperation;

      const result = getHarvestErrorInfo(operation);

      expect(result?.title).toBe('Harvest Failed');
      expect(result?.message).toBe('Something went wrong');
      expect(result?.iconType).toBe('unknown');
      expect(result?.canRetry).toBe(false);
      expect(result?.suggestion).toBeUndefined();
    });

    it('should handle error without errorType', () => {
      const operation: GcodeHarvestOperation = {
        id: 'op-7',
        status: 'Failed',
        error: 'Generic error message',
      } as GcodeHarvestOperation;

      const result = getHarvestErrorInfo(operation);

      expect(result?.title).toBe('Harvest Failed');
      expect(result?.message).toBe('Generic error message');
      expect(result?.iconType).toBe('unknown');
    });

    it('should default isRetryable to false when not specified', () => {
      const operation: GcodeHarvestOperation = {
        id: 'op-8',
        status: 'Failed',
        error: 'Some error',
        errorType: 'ConnectionError',
      } as GcodeHarvestOperation;

      const result = getHarvestErrorInfo(operation);

      expect(result?.canRetry).toBe(false);
    });

    it('should include all error phases', () => {
      const phases = ['Discovery', 'Download', 'Processing', 'Completion'];
      
      phases.forEach((phase) => {
        const operation: GcodeHarvestOperation = {
          id: `op-${phase}`,
          status: 'Failed',
          error: 'Error message',
          errorType: 'ConnectionError',
          errorPhase: phase,
        } as GcodeHarvestOperation;

        const result = getHarvestErrorInfo(operation);

        expect(result?.phase).toBe(phase);
      });
    });
  });

  describe('getPhaseDisplay', () => {
    it('should format Discovery phase', () => {
      expect(getPhaseDisplay('Discovery')).toBe('during file discovery');
    });

    it('should format Download phase', () => {
      expect(getPhaseDisplay('Download')).toBe('during file download');
    });

    it('should format Processing phase', () => {
      expect(getPhaseDisplay('Processing')).toBe('during file processing');
    });

    it('should format Completion phase', () => {
      expect(getPhaseDisplay('Completion')).toBe('during completion');
    });

    it('should return empty string for undefined phase', () => {
      expect(getPhaseDisplay(undefined)).toBe('');
      expect(getPhaseDisplay('')).toBe('');
    });

    it('should return empty string for unknown phase', () => {
      expect(getPhaseDisplay('UnknownPhase')).toBe('');
      expect(getPhaseDisplay('SomePhase')).toBe('');
    });
  });
});
