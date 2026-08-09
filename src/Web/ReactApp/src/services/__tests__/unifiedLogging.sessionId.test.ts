import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';

/**
 * Regression test for the js/insecure-randomness CodeQL finding: the
 * per-tab session correlation id used to be derived from `Math.random()`,
 * a non-cryptographic PRNG. It carries no security meaning (it only tags
 * log entries and telemetry spans for grouping), but there's no reason to
 * use a weak generator when `crypto.randomUUID()` is free, so the fix
 * switches to the shared `generateUUID()` helper.
 */
describe('unifiedLogger session id', () => {
  beforeEach(() => {
    vi.resetModules();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('derives the session id from a UUID, not Math.random()', async () => {
    const randomSpy = vi.spyOn(Math, 'random');
    const { unifiedLogger } = await import('../unifiedLogging');

    const stored = unifiedLogger.getStoredLogs();
    void stored;

    // Session id format: frontend-session-<epoch-ms>-<uuid-v4>
    const sessionIdMatch = /^frontend-session-\d+-[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

    // Trigger a log call, which persists the current sessionId onto the entry.
    unifiedLogger.info('test message');
    const logs = unifiedLogger.getStoredLogs();
    const last = logs[logs.length - 1];

    expect(last.sessionId).toBeDefined();
    expect(last.sessionId as string).toMatch(sessionIdMatch);
    expect(randomSpy).not.toHaveBeenCalled();

    unifiedLogger.clearStoredLogs();
    unifiedLogger.restoreConsole();
  });
});
