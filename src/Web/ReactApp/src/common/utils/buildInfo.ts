/**
 * Build metadata baked into the frontend bundle at build time.
 *
 * `commit` is the short git SHA of the source commit (or the injected `VITE_GIT_SHA`
 * in container builds; `'dev'` when neither git nor the build arg is available). This
 * mirrors the backend `/api/system/version` `commit` field and the `commit` value in
 * the emitted `/version.json`, so the deployed frontend commit can be verified.
 */
export interface BuildInfo {
  commit: string;
  buildTime: string;
}

export const buildInfo: BuildInfo = {
  commit: typeof __GIT_HASH__ !== 'undefined' ? __GIT_HASH__ : 'dev',
  buildTime: typeof __BUILD_TIME__ !== 'undefined' ? __BUILD_TIME__ : new Date(0).toISOString(),
};
