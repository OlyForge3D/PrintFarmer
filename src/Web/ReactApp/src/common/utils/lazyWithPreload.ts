import React from 'react';

// Helper to allow manual preloading of a lazily loaded component (code-split chunk)
// Usage:
//   const MyComp = lazyWithPreload(() => import('./MyComp'));
//   MyComp.preload(); // (e.g. on hover) to start loading before render
export interface PreloadableComponent<P extends object, T extends React.ComponentType<P>> {
  preload: () => Promise<{ default: T }>;
  retry: () => void;
}

export function lazyWithPreload<P extends object, T extends React.ComponentType<P>>(
  factory: () => Promise<{ default: T }>
): React.ComponentType<P> & PreloadableComponent<P, T> {
  let modulePromise: Promise<{ default: T }> | undefined;
  const load = (): Promise<{ default: T }> => {
    modulePromise ??= factory().catch((error: unknown) => {
      modulePromise = undefined;
      throw error;
    });
    return modulePromise;
  };
  const loadComponent = (): Promise<{ default: React.ComponentType<P> }> => load();
  let LazyComponent = React.lazy(loadComponent);
  const RetryableComponent: React.FC<P> = (props) => React.createElement(LazyComponent, props);

  return Object.assign(RetryableComponent, {
    preload: load,
    retry: () => {
      modulePromise = undefined;
      LazyComponent = React.lazy(loadComponent);
    },
  });
}
