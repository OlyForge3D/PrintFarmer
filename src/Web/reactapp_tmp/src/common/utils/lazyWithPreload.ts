import React from 'react';

// Helper to allow manual preloading of a lazily loaded component (code-split chunk)
// Usage:
//   const MyComp = lazyWithPreload(() => import('./MyComp'));
//   MyComp.preload(); // (e.g. on hover) to start loading before render
export interface PreloadableComponent<P, T extends React.ComponentType<P>> {
  (props: P): React.ReactElement | null;
  preload: () => Promise<{ default: T }>;
}

export function lazyWithPreload<P, T extends React.ComponentType<P>>(
  factory: () => Promise<{ default: T }>
): T & PreloadableComponent<P, T> {
  const LazyComp = React.lazy(factory) as unknown as T & PreloadableComponent<P, T>;
  LazyComp.preload = factory;
  return LazyComp;
}
