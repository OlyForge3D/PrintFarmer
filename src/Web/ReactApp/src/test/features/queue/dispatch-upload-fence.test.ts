import { describe, expect, it } from 'vitest';
import {
  advanceDispatchUploadFence,
  fenceDispatchAttempt,
  type DispatchUploadFence,
} from '@/features/queue/utils/dispatchUploadFence';
import type { DispatchUploadProgressDto } from '@/types/api';

function progress(
  attemptId: string,
  attemptNumber: number,
  sequence: number,
  isCompleted = false
): DispatchUploadProgressDto {
  return {
    jobId: 'job-1',
    printerId: 'printer-1',
    attemptId,
    attemptNumber,
    sequence,
    fileName: 'calibration.gcode',
    bytesSent: sequence,
    totalBytes: 10,
    percentage: sequence * 10,
    isCompleted,
  };
}

describe('dispatch upload attempt fence', () => {
  it('rejects delayed terminal delivery from attempt A after B owns the job', () => {
    const current: DispatchUploadFence = {
      attemptId: 'attempt-b',
      attemptNumber: 2,
      sequence: 1,
    };

    expect(
      advanceDispatchUploadFence(
        current,
        progress('attempt-a', 1, 99, true)
      )
    ).toBeNull();
  });

  it('rejects duplicate, reordered, and same-number foreign-attempt events', () => {
    const current: DispatchUploadFence = {
      attemptId: 'attempt-b',
      attemptNumber: 2,
      sequence: 5,
    };

    expect(
      advanceDispatchUploadFence(current, progress('attempt-b', 2, 5))
    ).toBeNull();
    expect(
      advanceDispatchUploadFence(current, progress('attempt-b', 2, 4))
    ).toBeNull();
    expect(
      advanceDispatchUploadFence(current, progress('attempt-other', 2, 6))
    ).toBeNull();
  });

  it('resets per-attempt sequence when a newer dispatch attempt starts', () => {
    const attemptA: DispatchUploadFence = {
      attemptId: 'attempt-a',
      attemptNumber: 1,
      sequence: 20,
    };

    const attemptB = fenceDispatchAttempt(attemptA, 'attempt-b', 2);

    expect(attemptB.sequence).toBe(0);
    expect(
      advanceDispatchUploadFence(attemptB, progress('attempt-b', 2, 1))
    ).toEqual({
      attemptId: 'attempt-b',
      attemptNumber: 2,
      sequence: 1,
    });
  });

  it('retains the exact-attempt cursor across reconnect ordering', () => {
    const beforeReconnect: DispatchUploadFence = {
      attemptId: 'attempt-b',
      attemptNumber: 2,
      sequence: 7,
    };

    expect(
      advanceDispatchUploadFence(
        beforeReconnect,
        progress('attempt-a', 1, 100, true)
      )
    ).toBeNull();
    expect(
      advanceDispatchUploadFence(
        beforeReconnect,
        progress('attempt-b', 2, 8)
      )
    ).toEqual({
      attemptId: 'attempt-b',
      attemptNumber: 2,
      sequence: 8,
    });
  });
});
