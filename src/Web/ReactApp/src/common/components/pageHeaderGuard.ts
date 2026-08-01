/**
 * Development-only guard against duplicated page headers.
 *
 * A page mounted inside a shell that already renders page chrome must pass
 * `embedded` to `PageTemplate`. Forgetting to do so produces a second `h1`, a
 * second subtitle and a second page background — an accessibility defect that is
 * easy to introduce and invisible in a code review of a single file.
 *
 * Every non-embedded `PageTemplate` registers itself here. If two are ever
 * mounted at once, we say so, loudly, once.
 */

let mountedHeaders: string[] = [];
let hasWarned = false;

/**
 * Register a page header for the lifetime of a non-embedded `PageTemplate`.
 *
 * @param title The page title, used to name the offenders in the warning.
 * @returns A cleanup function to call on unmount.
 */
export function registerPageHeader(title: string): () => void {
  mountedHeaders = [...mountedHeaders, title];

  if (import.meta.env.DEV && mountedHeaders.length > 1 && !hasWarned) {
    hasWarned = true;
    console.warn(
      `[PageTemplate] More than one page header is mounted at once: ${mountedHeaders
        .map((name) => `"${name}"`)
        .join(', ')}. ` +
        'A page rendered inside a shell must pass `embedded` to PageTemplate, ' +
        'otherwise the document ends up with several h1 elements and stacked page chrome.',
    );
  }

  return () => {
    const index = mountedHeaders.indexOf(title);
    if (index !== -1) {
      mountedHeaders = [...mountedHeaders.slice(0, index), ...mountedHeaders.slice(index + 1)];
    }
  };
}

/** Number of non-embedded page headers currently mounted. Exposed for tests. */
export function mountedPageHeaderCount(): number {
  return mountedHeaders.length;
}

/** Clear all guard state. Tests must call this between cases. */
export function resetPageHeaderGuard(): void {
  mountedHeaders = [];
  hasWarned = false;
}
