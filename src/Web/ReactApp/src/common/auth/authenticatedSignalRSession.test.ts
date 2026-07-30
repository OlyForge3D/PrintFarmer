import { describe, expect, it, vi } from 'vitest';
import {
  registerAuthenticatedSignalRTransport,
  resetAuthenticatedSignalRSession,
} from './authenticatedSignalRSession';

describe('authenticated SignalR session reset', () => {
  it('awaits every secured singleton transport reset', async () => {
    const resets = {
      printerStatus: vi.fn().mockResolvedValue(undefined),
      harvest: vi.fn().mockResolvedValue(undefined),
      printerImport: vi.fn().mockResolvedValue(undefined),
      slicer: vi.fn().mockResolvedValue(undefined),
      maintenance: vi.fn().mockResolvedValue(undefined),
    };
    const unregister: Array<() => void> = [];
    for (const [name, reset] of Object.entries(resets)) {
      unregister.push(registerAuthenticatedSignalRTransport(name, reset));
    }

    await resetAuthenticatedSignalRSession();
    unregister.forEach(remove => remove());

    for (const reset of Object.values(resets)) {
      expect(reset).toHaveBeenCalledOnce();
    }
  });

  it('coalesces concurrent identity transitions into one transport reset', async () => {
    let releaseReset: (() => void) | undefined;
    const reset = vi.fn(() => new Promise<void>(resolve => {
      releaseReset = resolve;
    }));
    const unregister = registerAuthenticatedSignalRTransport('coalesced-transition', reset);

    const first = resetAuthenticatedSignalRSession();
    const second = resetAuthenticatedSignalRSession();
    await vi.waitFor(() => expect(reset).toHaveBeenCalledOnce());
    releaseReset?.();
    await Promise.all([first, second]);
    unregister();

    expect(reset).toHaveBeenCalledOnce();
  });

  it('does not reset a component-scoped transport after it unregisters', async () => {
    const reset = vi.fn().mockResolvedValue(undefined);
    const unregister = registerAuthenticatedSignalRTransport('component-transport', reset);

    unregister();
    await resetAuthenticatedSignalRSession();

    expect(reset).not.toHaveBeenCalled();
  });
});
