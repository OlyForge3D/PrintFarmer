/**
 * Test helper: precise, whitelist-based console.error suppression.
 *
 * Prior versions of these tests replaced `console.error` with an empty
 * `vi.fn()` for the duration of the suite. That silenced ALL React noise
 * — including render-phase warnings, act-boundary warnings, "please add
 * an error boundary" hints, and unexpected side-effect logs — which
 * hides genuine regressions behind the ones the test intended to
 * suppress.
 *
 * This helper installs a `console.error` shim that:
 *   1. Suppresses only messages matching the caller-provided allow-list.
 *   2. Captures every allowed call in an array the test can assert on.
 *   3. Records every UNALLOWED call so `flushUnexpectedErrors()` can
 *      fail the test in `afterEach` with a readable message. This turns
 *      an "unexpected console.error" into a real failure rather than
 *      silent noise buried in the test log.
 *
 * Usage inside a `describe`:
 *
 *   const consoleFilter = installConsoleErrorFilter([
 *     /^ErrorBoundary caught an error:$/,
 *     /^The above error occurred in/,
 *   ]);
 *   afterEach(() => consoleFilter.flushUnexpectedErrors());
 *
 * A concrete test may inspect `consoleFilter.allowedCalls` to assert
 * that the expected error surfaced (e.g. that `componentDidCatch` did
 * log our own tagged message).
 */
import { afterAll, afterEach, beforeAll, beforeEach, expect } from 'vitest';

export interface ConsoleErrorFilter {
  /** All calls whose FIRST argument matched an allow-list pattern. */
  readonly allowedCalls: unknown[][];
  /** Calls that DID NOT match any allow-list pattern. */
  readonly unexpectedCalls: unknown[][];
  /**
   * If any unexpected calls were captured since the last flush, throw
   * an assertion — call this from `afterEach` so an unexpected log
   * fails the current test rather than the whole suite.
   */
  flushUnexpectedErrors(): void;
}

/**
 * Normalize the arguments of a `console.error(...)` call to a string
 * we can pattern-match against. React 19 (and the boundary itself)
 * log errors in a few different shapes:
 *   - `console.error(errorObj)` — first arg is the Error.
 *   - `console.error('...tag:', errorObj, [componentStack])` — first
 *     arg is a tag string, second is the Error. We must be able to
 *     match on EITHER piece.
 *   - `console.error('%s the above error occurred in', errorObj,
 *      componentStack)` — printf-style format string.
 *   - `console.error('...', 'a string', 'another string')` — plain
 *     multi-arg text.
 *
 * We build a single haystack string containing every argument as text
 * (Error → its `message`, everything else → `String(x)`) so patterns
 * can match on any part of the call.
 */
function argsAsHaystack(args: unknown[]): string {
  return args
    .map(arg => {
      if (typeof arg === 'string') return arg;
      if (arg instanceof Error) return arg.message;
      try {
        return String(arg);
      } catch {
        return '';
      }
    })
    .join(' ');
}

/**
 * Install a precise console.error filter for the enclosing `describe`.
 * `allowedPatterns` is matched against the FIRST argument of every
 * `console.error(...)` call. Anything unmatched is recorded and, on
 * `flushUnexpectedErrors()`, causes the test to fail.
 *
 * The original console.error is restored in `afterAll` so subsequent
 * suites see the real console.
 */
export function installConsoleErrorFilter(
  allowedPatterns: readonly RegExp[],
): ConsoleErrorFilter {
  const allowed: unknown[][] = [];
  const unexpected: unknown[][] = [];
  let original: typeof console.error;

  beforeAll(() => {
    original = console.error;
    console.error = (...args: unknown[]) => {
      const haystack = argsAsHaystack(args);
      const matched = allowedPatterns.some(rx => rx.test(haystack));
      if (matched) {
        allowed.push(args);
      } else {
        unexpected.push(args);
        // Also forward unexpected calls to the real console so the
        // test output shows them alongside the assertion failure —
        // easier to diagnose when a new React warning appears.
        original(...args);
      }
    };
  });

  // Reset both buffers before every test so allowed-call assertions
  // (see the "should log error to console" test) only inspect calls
  // from the current test, not accumulated state from previous ones.
  // Unexpected calls are also drained here — the previous test's
  // afterEach already flushed them into a failure, so keeping them
  // would double-report.
  beforeEach(() => {
    allowed.length = 0;
    unexpected.length = 0;
  });

  // Backstop: if a test forgot to call `flushUnexpectedErrors()` in
  // its own afterEach, still fail on unexpected calls. Registering
  // this INSIDE the installer means every describe that uses the
  // filter gets the guard automatically.
  afterEach(() => {
    if (unexpected.length === 0) return;
    const dump = unexpected
      .map(call => argsAsHaystack(call))
      .join('\n  ');
    unexpected.length = 0;
    expect.fail(
      `Unexpected console.error call(s) during test:\n  ${dump}`,
    );
  });

  afterAll(() => {
    console.error = original;
  });

  return {
    allowedCalls: allowed,
    unexpectedCalls: unexpected,
    flushUnexpectedErrors() {
      if (unexpected.length === 0) return;
      const dump = unexpected
        .map(call => argsAsHaystack(call))
        .join('\n  ');
      // Consume the buffer so subsequent tests aren't blamed for the
      // same unexpected calls.
      unexpected.length = 0;
      expect.fail(
        `Unexpected console.error call(s) during test:\n  ${dump}`,
      );
    },
  };
}
