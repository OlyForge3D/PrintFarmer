import React, { Suspense } from 'react';
import { lazyWithPreload } from '@/common/utils/lazyWithPreload';

type STLPreviewModalComponent = typeof import(
  '@/features/models3d/components/3d/STLPreviewModal'
)['STLPreviewModal'];
type STLPreviewModalProps = React.ComponentProps<STLPreviewModalComponent>;

const STLPreviewModal = lazyWithPreload<
  STLPreviewModalProps,
  STLPreviewModalComponent
>(
  () => import('@/features/models3d/components/3d/STLPreviewModal')
    .then((module) => ({ default: module.STLPreviewModal })),
);

export function STLPreviewModalBoundary(props: STLPreviewModalProps) {
  return (
    <Suspense fallback={null}>
      <STLPreviewModal {...props} />
    </Suspense>
  );
}
