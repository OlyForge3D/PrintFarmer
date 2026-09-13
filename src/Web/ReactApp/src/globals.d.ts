// Compile-time constants injected by Vite's `define` (see vite.config.ts).
declare const __GIT_HASH__: string;
declare const __BUILD_TIME__: string;

/** Shared build record supplied by the release authority, never derived from package.json. */
declare const __RELEASE_IDENTITY__: import('./types/api').CanonicalReleaseIdentity | null;
