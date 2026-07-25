import { toast, type ExternalToast } from 'sonner';

/**
 * Admin surface toast helpers. Thin wrapper around `sonner` so admin pages don't
 * reach for the vendor directly and so we can keep the surface consistent (position,
 * duration, richColors) with the `<Toaster />` mounted in `App.tsx`.
 *
 * Prefer these over `toast.*` in admin pages so future changes (e.g. adding tracking,
 * changing the vendor) land in one place.
 */
export const adminToast = {
  /** Green toast for confirmed success (e.g. "Settings saved"). */
  success(message: string, options?: AdminToastOptions): string | number {
    return toast.success(message, options);
  },

  /**
   * Red toast for failed operations. Include an action to retry or open logs when
   * possible. Never use `window.alert()` — this replaces it.
   */
  error(message: string, options?: AdminToastOptions): string | number {
    return toast.error(message, options);
  },

  /** Neutral toast for informational messages. */
  info(message: string, options?: AdminToastOptions): string | number {
    return toast.info(message, options);
  },

  /** Amber toast for warnings — action needed but not blocking. */
  warning(message: string, options?: AdminToastOptions): string | number {
    return toast.warning(message, options);
  },
};

/** Options passthrough to sonner. Re-exported so callers stay off the vendor path. */
export type AdminToastOptions = ExternalToast;

export type AdminToast = typeof adminToast;
