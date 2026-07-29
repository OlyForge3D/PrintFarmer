function mutationErrorValue(error: unknown) {
  return typeof error === 'object' && error
    ? (error as {
        message?: string;
        details?: string;
        statusCode?: number;
        response?: { status?: number };
        data?: { detail?: string };
      })
    : undefined;
}

export function mutationErrorStatus(error: unknown): number | undefined {
  const value = mutationErrorValue(error);
  return value?.statusCode ?? value?.response?.status;
}

export function mutationErrorMessage(error: unknown, fallback: string): string {
  const value = mutationErrorValue(error);
  const status = mutationErrorStatus(error);
  const detail = value?.data?.detail ?? value?.details;

  if (status === 412) {
    return detail
      ? `This item changed after you reviewed it: ${detail}`
      : 'This item changed after you reviewed it. Refresh and review before confirming again.';
  }
  if (status === 428) {
    return detail
      ? `A reviewed revision is required: ${detail}`
      : 'A reviewed revision is required. Refresh and review before confirming again.';
  }

  return value?.message || detail || (error instanceof Error ? error.message : fallback);
}
