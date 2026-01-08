// Global ambient declarations for Vite path aliases so tests don't need ts-expect-error comments.
// This keeps ESLint happy and documents our supported import patterns.

/// <reference types="vite/client" />

declare module '@/components/*';
declare module '@/hooks/*';
declare module '@/services/*';
declare module '@/types/*';
declare module '@/utils/*';
declare module '@/pages/*';

// Provide minimal typings for injected globals used by context indirection.
// Removed legacy window global context references (__PF_AUTH_CTX__, __PF_THEME_CTX__).
