export type AuthenticatedSignalRReset = () => Promise<void>;

const authenticatedTransports = new Map<string, AuthenticatedSignalRReset>();
let activeReset: Promise<void> | null = null;

export function registerAuthenticatedSignalRTransport(
  name: string,
  reset: AuthenticatedSignalRReset,
): void {
  authenticatedTransports.set(name, reset);
}

export async function resetAuthenticatedSignalRSession(): Promise<void> {
  if (!activeReset) {
    activeReset = resetRegisteredTransports().finally(() => {
      activeReset = null;
    });
  }

  await activeReset;
}

async function resetRegisteredTransports(): Promise<void> {
  const results = await Promise.allSettled(
    [...authenticatedTransports].map(async ([name, reset]) => {
      await reset();
      return name;
    }),
  );
  const failures = results.flatMap((result, index) => {
    if (result.status === 'fulfilled') {
      return [];
    }

    const name = [...authenticatedTransports.keys()][index] ?? 'unknown';
    const cause = result.reason instanceof Error
      ? result.reason
      : new Error(String(result.reason));
    return [new Error(`Failed to reset authenticated SignalR transport "${name}".`, { cause })];
  });

  if (failures.length > 0) {
    throw new AggregateError(failures, 'Authenticated SignalR session reset failed.');
  }
}
