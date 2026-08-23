import { describe, it, expect } from 'vitest';
import { formatQueuePositionSuffix } from '@/services/sliceJobService';

// Regression coverage for issue #1869: the /slicer queued-job confirmation
// message must not display a literal "null" when the API omits the queue
// position. Reproduces two sequential queued jobs — one where the API
// returns a position, and one where it doesn't.
describe('formatQueuePositionSuffix (issue #1869)', () => {
  it('formats a valid queue position for the first queued job', () => {
    // First job: POST /api/slice/ succeeds (201) and returns queuePosition: 2.
    expect(formatQueuePositionSuffix(2)).toBe(' position 2');
  });

  it('omits the position phrase entirely for a second queued job with no position', () => {
    // Second job: POST /api/slice/ succeeds (201) but queuePosition is null.
    expect(formatQueuePositionSuffix(null)).toBe('');
  });

  it('never returns a string containing the literal word "null"', () => {
    expect(formatQueuePositionSuffix(null)).not.toMatch(/\bnull\b/);
    expect(formatQueuePositionSuffix(undefined)).not.toMatch(/\bnull\b/);
    expect(formatQueuePositionSuffix(0)).not.toMatch(/\bnull\b/);
  });

  it('supports a custom separator for callers with a different message format', () => {
    expect(formatQueuePositionSuffix(5, ' — position ')).toBe(' — position 5');
    expect(formatQueuePositionSuffix(null, ' — position ')).toBe('');
  });

  it('treats position 0 as a valid position, not an absent one', () => {
    expect(formatQueuePositionSuffix(0)).toBe(' position 0');
  });
});
