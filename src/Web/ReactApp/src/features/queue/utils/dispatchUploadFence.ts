import type { DispatchUploadProgressDto } from '@/types/api';

export interface DispatchUploadFence {
  attemptId: string;
  attemptNumber: number;
  sequence: number;
}

export function fenceDispatchAttempt(
  current: DispatchUploadFence | undefined,
  attemptId: string,
  attemptNumber: number
): DispatchUploadFence {
  if (current && attemptNumber < current.attemptNumber) {
    return current;
  }

  const sameAttempt =
    current?.attemptId === attemptId &&
    current.attemptNumber === attemptNumber;
  return {
    attemptId,
    attemptNumber,
    sequence: sameAttempt ? current.sequence : 0,
  };
}

export function advanceDispatchUploadFence(
  current: DispatchUploadFence | undefined,
  progress: DispatchUploadProgressDto
): DispatchUploadFence | null {
  if (
    current &&
    (progress.attemptNumber < current.attemptNumber ||
      (progress.attemptNumber === current.attemptNumber &&
        (progress.attemptId !== current.attemptId ||
          progress.sequence <= current.sequence)))
  ) {
    return null;
  }

  return {
    attemptId: progress.attemptId,
    attemptNumber: progress.attemptNumber,
    sequence: progress.sequence,
  };
}
