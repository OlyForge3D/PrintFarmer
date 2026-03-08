import { describe, it, expect } from 'vitest';

// Mock utility that will be created by other agents
// This utility extracts status indicator color logic from components
const getStatusIndicatorColor = (
  state: string | undefined,
  isOnline: boolean | undefined
): string => {
  // Implementation based on PrinterCard patterns
  const actualState = state ?? 'Unknown';
  const actualOnline = isOnline ?? false;

  // Offline takes precedence
  if (!actualOnline) {
    return 'bg-pf-text-tertiary opacity-50';
  }

  // State-specific colors using pf-* tokens
  switch (actualState.toLowerCase()) {
    case 'idle':
      return 'bg-pf-success';
    case 'printing':
      return 'bg-pf-accent pf-animate-pulse'; // Uses pf-animate-pulse, not raw animate-pulse
    case 'paused':
      return 'bg-pf-warning';
    case 'error':
    case 'halted':
      return 'bg-pf-error';
    case 'complete':
      return 'bg-pf-success';
    case 'offline':
    case 'shutdown':
      return 'bg-pf-text-tertiary opacity-50';
    default:
      return 'bg-pf-text-secondary';
  }
};

describe('Status Color Extraction Utility', () => {
  describe('getStatusIndicatorColor', () => {
    it('returns correct classes for idle state', () => {
      const result = getStatusIndicatorColor('Idle', true);
      expect(result).toBe('bg-pf-success');
    });

    it('returns correct classes for printing state with animation', () => {
      const result = getStatusIndicatorColor('Printing', true);
      expect(result).toContain('bg-pf-accent');
      expect(result).toContain('pf-animate-pulse');
      // Should use pf-animate-pulse, not raw animate-pulse (without pf- prefix)
      const classes = result.split(' ');
      const hasRawAnimatePulse = classes.includes('animate-pulse');
      expect(hasRawAnimatePulse).toBe(false);
    });

    it('returns correct classes for paused state', () => {
      const result = getStatusIndicatorColor('Paused', true);
      expect(result).toBe('bg-pf-warning');
    });

    it('returns correct classes for error state', () => {
      const result = getStatusIndicatorColor('Error', true);
      expect(result).toBe('bg-pf-error');
    });

    it('returns correct classes for halted state', () => {
      const result = getStatusIndicatorColor('Halted', true);
      expect(result).toBe('bg-pf-error');
    });

    it('returns correct classes for complete state', () => {
      const result = getStatusIndicatorColor('Complete', true);
      expect(result).toBe('bg-pf-success');
    });

    it('returns correct classes for offline state', () => {
      const result = getStatusIndicatorColor('Offline', true);
      expect(result).toContain('bg-pf-text-tertiary');
      expect(result).toContain('opacity-50');
    });

    it('returns correct classes for shutdown state', () => {
      const result = getStatusIndicatorColor('Shutdown', true);
      expect(result).toContain('bg-pf-text-tertiary');
      expect(result).toContain('opacity-50');
    });

    it('returns offline styling when isOnline=false regardless of state', () => {
      const resultIdle = getStatusIndicatorColor('Idle', false);
      expect(resultIdle).toContain('bg-pf-text-tertiary');
      expect(resultIdle).toContain('opacity-50');

      const resultPrinting = getStatusIndicatorColor('Printing', false);
      expect(resultPrinting).toContain('bg-pf-text-tertiary');
      expect(resultPrinting).toContain('opacity-50');

      const resultComplete = getStatusIndicatorColor('Complete', false);
      expect(resultComplete).toContain('bg-pf-text-tertiary');
      expect(resultComplete).toContain('opacity-50');
    });

    it('returns animate-pulse only for printing state', () => {
      const printingResult = getStatusIndicatorColor('Printing', true);
      expect(printingResult).toContain('pf-animate-pulse');

      const idleResult = getStatusIndicatorColor('Idle', true);
      expect(idleResult).not.toContain('pf-animate-pulse');

      const pausedResult = getStatusIndicatorColor('Paused', true);
      expect(pausedResult).not.toContain('pf-animate-pulse');
    });

    it('uses only pf-* token classes (no raw Tailwind colors)', () => {
      const states = ['Idle', 'Printing', 'Paused', 'Error', 'Complete', 'Offline'];
      
      states.forEach(state => {
        const result = getStatusIndicatorColor(state, true);
        
        // Should use pf-* prefixed classes
        const hasPfToken = 
          result.includes('bg-pf-') || 
          result.includes('text-pf-') || 
          result.includes('pf-animate-');
        expect(hasPfToken).toBe(true);
        
        // Should NOT use raw Tailwind color classes
        expect(result).not.toContain('bg-green-');
        expect(result).not.toContain('bg-blue-');
        expect(result).not.toContain('bg-yellow-');
        expect(result).not.toContain('bg-red-');
        expect(result).not.toContain('bg-gray-');
      });
    });

    it('handles unknown/undefined states gracefully', () => {
      const undefinedResult = getStatusIndicatorColor(undefined, true);
      expect(undefinedResult).toBe('bg-pf-text-secondary');

      const unknownResult = getStatusIndicatorColor('SomeUnknownState', true);
      expect(unknownResult).toBe('bg-pf-text-secondary');
    });

    it('is case-insensitive for state names', () => {
      const lowerIdle = getStatusIndicatorColor('idle', true);
      const upperIdle = getStatusIndicatorColor('IDLE', true);
      const mixedIdle = getStatusIndicatorColor('IdLe', true);

      expect(lowerIdle).toBe('bg-pf-success');
      expect(upperIdle).toBe('bg-pf-success');
      expect(mixedIdle).toBe('bg-pf-success');
    });

    it('handles undefined isOnline as false', () => {
      const result = getStatusIndicatorColor('Printing', undefined);
      expect(result).toContain('bg-pf-text-tertiary');
      expect(result).toContain('opacity-50');
    });
  });

  describe('Color Consistency Across States', () => {
    it('uses bg-pf-success for positive states (idle, complete)', () => {
      expect(getStatusIndicatorColor('Idle', true)).toContain('bg-pf-success');
      expect(getStatusIndicatorColor('Complete', true)).toContain('bg-pf-success');
    });

    it('uses bg-pf-error for error states', () => {
      expect(getStatusIndicatorColor('Error', true)).toContain('bg-pf-error');
      expect(getStatusIndicatorColor('Halted', true)).toContain('bg-pf-error');
    });

    it('uses bg-pf-warning for cautionary states', () => {
      expect(getStatusIndicatorColor('Paused', true)).toContain('bg-pf-warning');
    });

    it('uses bg-pf-accent for active states', () => {
      expect(getStatusIndicatorColor('Printing', true)).toContain('bg-pf-accent');
    });

    it('uses bg-pf-text-tertiary with opacity for offline/unavailable states', () => {
      const offlineStates = ['Offline', 'Shutdown'];
      offlineStates.forEach(state => {
        const result = getStatusIndicatorColor(state, true);
        expect(result).toContain('bg-pf-text-tertiary');
        expect(result).toContain('opacity-50');
      });
    });
  });
});
