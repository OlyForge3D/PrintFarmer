/**
 * Shared admin UI primitives.
 *
 * These primitives replace the ad-hoc loading, empty, error, and save patterns
 * that had grown up across the admin surface. Downstream admin rebuilds
 * (#935 settings, #936 hub) consume this module.
 *
 * DO NOT import individual files directly — always go through this barrel so we
 * can move implementations without breaking callers.
 */

export { AdminLoading } from './AdminLoading';
export type { AdminLoadingProps, AdminLoadingVariant } from './AdminLoading';

export { AdminEmpty } from './AdminEmpty';
export type { AdminEmptyProps } from './AdminEmpty';

export { AdminError } from './AdminError';
export type { AdminErrorProps } from './AdminError';

export { AdminSaveBar } from './AdminSaveBar';
export type { AdminSaveBarProps } from './AdminSaveBar';

export { useDirtyState, isStructurallyEqual } from './useDirtyState';
export type { UseDirtyStateOptions, UseDirtyStateResult } from './useDirtyState';

export { adminToast } from './adminToast';
export type { AdminToastOptions } from './adminToast';
