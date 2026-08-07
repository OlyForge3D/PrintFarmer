import React, { Suspense } from 'react';
import { lazyWithPreload } from '@/common/utils/lazyWithPreload';
import type { SlicerWorkspaceProps } from '@/features/slicer/components/viewer/SlicerWorkspace';

type SlicerWorkspaceComponent = typeof import(
  '@/features/slicer/components/viewer/SlicerWorkspace'
)['SlicerWorkspace'];

const SlicerWorkspace = lazyWithPreload<
  React.ComponentProps<SlicerWorkspaceComponent>,
  SlicerWorkspaceComponent
>(
  () => import('@/features/slicer/components/viewer/SlicerWorkspace')
    .then((module) => ({ default: module.SlicerWorkspace })),
);

export function SlicerWorkspaceBoundary(props: SlicerWorkspaceProps) {
  return (
    <Suspense
      fallback={(
        <div
          className="flex h-full w-full items-center justify-center"
          role="status"
          aria-label="Loading slicer workspace"
        >
          <div className="pf-animate-spin h-8 w-8 rounded-full border-b-2 border-pf-accent" />
        </div>
      )}
    >
      <SlicerWorkspace {...props} />
    </Suspense>
  );
}
