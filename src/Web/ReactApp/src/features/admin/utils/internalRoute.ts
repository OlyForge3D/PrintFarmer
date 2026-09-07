/**
 * Validation for app-relative routes that arrive as untrusted payload.
 *
 * This logic was introduced by #2526 in `AdminControlCenterPage`, which was then
 * the only place that rendered `AttentionItemDto.actionRoute` into a link. #2517
 * moved attention rendering into `AdminAttentionPanel`, so the validator lives
 * here rather than in either component: a single implementation with one obvious
 * home, so a future move of the call site cannot leave a hardened copy behind
 * uncalled while a weaker check quietly takes over the real rendering path.
 */

/**
 * Synthetic origin used only to run the WHATWG URL parser over an app-relative
 * route. It is never navigated to; `.invalid` is reserved by RFC 2606 precisely
 * so it can never resolve to a real host.
 */
const INTERNAL_ROUTE_ORIGIN = 'https://printfarmer.invalid';

/** Parse an app-relative route against the synthetic origin. `null` if it isn't same-origin. */
function parseInternalRoute(route: string): URL | null {
  try {
    const parsed = new URL(route, INTERNAL_ROUTE_ORIGIN);
    return parsed.origin === INTERNAL_ROUTE_ORIGIN ? parsed : null;
  } catch {
    return null;
  }
}

/** Percent-decode a pathname the way the router does when matching. `null` on malformed encoding. */
function decodeRoutePathname(pathname: string): string | null {
  try {
    return decodeURIComponent(pathname);
  } catch {
    return null;
  }
}

/**
 * Reduce a route to the pathname React Router will actually match on, so route
 * *identity* checks cannot be evaded by an equivalent spelling.
 *
 * Normalising by hand is not enough here, because a browser applies full URL
 * semantics to an href before the router ever sees it: `/foo/../admin` resolves
 * to `/admin`, `/admin\` folds to `/admin/`, and `/%61dmin` decodes to `/admin`.
 * A lexical check misses all three and would emit exactly the self-link Issue
 * 2526 forbids. So delegate to the URL parser — the same algorithm the browser
 * uses — then decode, fold trailing slashes, and lowercase for the comparison
 * (React Router matches case-insensitively and treats a trailing slash as
 * equivalent).
 */
function routePathname(route: string): string {
  const parsed = parseInternalRoute(route);
  let pathname: string;
  if (parsed) {
    pathname = parsed.pathname;
  } else {
    // Off-origin or unparseable: it is not an in-app route, so it can never be
    // an in-app route *identity*. Strip query/hash lexically and let the
    // comparison fall through to "not a match".
    const queryOrHash = route.search(/[?#]/);
    pathname = queryOrHash === -1 ? route : route.slice(0, queryOrHash);
  }
  const decoded = decodeRoutePathname(pathname) ?? pathname;
  const withoutTrailingSlash = decoded.length > 1 ? decoded.replace(/\/+$/, '') : decoded;
  return withoutTrailingSlash.toLowerCase();
}

/** `/admin` itself, however spelled — but not `/admin/status` (a legitimate child) or `/admin-something` (an unrelated sibling). */
export function isControlCenterSelfRoute(route: string): boolean {
  return routePathname(route) === '/admin';
}

/** `/admin/manage` was retired and is not a registered route; never link to it. */
function isRetiredManageRoute(route: string): boolean {
  const pathname = routePathname(route);
  return pathname === '/admin/manage' || pathname.startsWith('/admin/manage/');
}

/**
 * `actionRoute` is untrusted backend payload rendered straight into a link
 * target, so prove it is an in-app route before it becomes one. Anything that
 * is not plainly app-relative is dropped rather than sanitised — a malformed or
 * compromised payload should make the link disappear (a visible failure), never
 * navigate somewhere unexpected.
 */
export function canonicalizeInternalRoute(rawRoute: string | null | undefined): string | null {
  if (!rawRoute) {
    return null;
  }
  const route = rawRoute.trim();

  // Must be app-relative. A single leading slash rejects absolute URLs
  // ("https://evil.test/x") and non-HTTP schemes ("javascript:alert(1)").
  if (!route.startsWith('/')) {
    return null;
  }
  // "//evil.test/x" is protocol-relative and navigates off-origin despite the
  // leading slash; browsers also fold backslashes into slashes, so "/\evil.test"
  // is the same attack spelled differently.
  if (route.startsWith('//') || route.startsWith('/\\')) {
    return null;
  }
  // Control characters can be stripped by the browser after our check runs,
  // changing what the string means. Reject rather than guess.
  // eslint-disable-next-line no-control-regex
  if (/[\u0000-\u001F\u007F]/.test(route)) {
    return null;
  }
  // Browsers fold a literal backslash into a path separator for HTTP(S) URLs,
  // so "/admin\" is really "/admin/". No legitimate in-app route contains one.
  if (route.includes('\\')) {
    return null;
  }

  // Run the browser's own URL algorithm rather than trusting the raw string:
  // it resolves dot segments ("/foo/../admin" -> "/admin") and re-rejects
  // anything that escapes to another origin.
  const parsed = parseInternalRoute(route);
  if (!parsed) {
    return null;
  }
  // Malformed percent-encoding: we cannot know what the router will match, so drop it.
  if (decodeRoutePathname(parsed.pathname) === null) {
    return null;
  }

  if (isRetiredManageRoute(route) || isControlCenterSelfRoute(route)) {
    return null;
  }

  const canonical = `${parsed.pathname}${parsed.search}${parsed.hash}`;

  // Re-validate what is actually emitted, because normalisation can *create* a
  // hostile route from an app-relative one: dot-segment resolution pops the
  // segment before an empty segment, so "/foo/..//evil.test/steal" normalises
  // to the protocol-relative "//evil.test/steal" while still parsing as
  // same-origin against the synthetic base. Checking only the input would let
  // that through as an off-origin link.
  if (!canonical.startsWith('/') || canonical.startsWith('//') || canonical.startsWith('/\\')) {
    return null;
  }
  // Canonicalisation must be a fixed point: if re-parsing the emitted string
  // yields anything different, it was not canonical and the browser could
  // resolve it to a route these guards never approved.
  const reparsed = parseInternalRoute(canonical);
  if (!reparsed || `${reparsed.pathname}${reparsed.search}${reparsed.hash}` !== canonical) {
    return null;
  }

  return canonical;
}
