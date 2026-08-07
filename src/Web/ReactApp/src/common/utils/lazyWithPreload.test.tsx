import React from 'react';
import { describe, expect, it, vi } from 'vitest';
import { lazyWithPreload } from './lazyWithPreload';

describe('lazyWithPreload', () => {
  it('clears a rejected preload so the module can be retried', async () => {
    const Component = () => <div>Loaded</div>;
    const factory = vi.fn()
      .mockRejectedValueOnce(new Error('Temporary chunk failure'))
      .mockResolvedValueOnce({ default: Component });
    const LazyComponent = lazyWithPreload(factory);

    await expect(LazyComponent.preload()).resolves.toBeUndefined();
    await expect(LazyComponent.preload()).resolves.toBeUndefined();

    expect(factory).toHaveBeenCalledTimes(2);
  });
});
