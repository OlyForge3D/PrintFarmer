import { describe, expect, it } from 'vitest';
import { mutationErrorMessage } from '@/common/utils/mutationError';

describe('mutationErrorMessage', () => {
  it('requires review after stale 412', () => {
    expect(
      mutationErrorMessage(
        { statusCode: 412, data: { detail: 'printer_revision_conflict' } },
        'fallback'
      )
    ).toBe(
      'This item changed after you reviewed it: printer_revision_conflict'
    );
  });

  it('requires a refreshed revision after 428', () => {
    expect(
      mutationErrorMessage(
        { statusCode: 428, data: { detail: 'If-Match is required.' } },
        'fallback'
      )
    ).toBe('A reviewed revision is required: If-Match is required.');
  });
});
