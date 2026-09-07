/**
 * Build metadata baked into the frontend bundle at build time.
 *
 * `commit` is the full git SHA of the source commit. Production builds fail when
 * neither Git nor an injected `VITE_GIT_SHA`/`GIT_SHA` provides that identity.
 * Development servers may use `'dev'`. The value mirrors the backend
 * `/api/system/version` `commit` field and the emitted `/version.json`.
 */
export interface BuildInfo {
  commit: string;
  buildTime: string;
}

export const buildInfo: BuildInfo = {
  commit: typeof __GIT_HASH__ !== 'undefined' ? __GIT_HASH__ : 'dev',
  buildTime: typeof __BUILD_TIME__ !== 'undefined' ? __BUILD_TIME__ : new Date(0).toISOString(),
};
