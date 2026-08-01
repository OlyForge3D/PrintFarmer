import type { ComponentType, ReactNode } from 'react';
import { PageTemplate, type PageParent } from '@/common/components/PageTemplate';
import { ADMIN_HUB_PARENT } from '@/features/admin/registry/adminDestinations';

export interface AdminPageShellProps {
  /** Page title. Rendered as the page's single `h1`. */
  title: string;
  /** One-line description of what this page is for. */
  subtitle?: string;
  /** Icon rendered before the title, matching the destination's registry icon. */
  icon?: ComponentType<{ className?: string }>;
  /** Page-level actions. This is the only place admin pages put action buttons. */
  actions?: ReactNode;
  /** Control rendered inline to the right of the title. */
  titleActions?: ReactNode;
  /**
   * Override the back link. Defaults to the Admin Control Center.
   * Pass `null` for the hub itself, which has no parent.
   */
  parent?: PageParent | null;
  /**
   * Render content only, with no header or background. Used when the page is
   * mounted inside a shell that already supplies page chrome.
   */
  embedded?: boolean;
  /** Content width. Matches the Control Center by default. */
  maxWidth?: 'max-w-3xl' | 'max-w-4xl' | 'max-w-5xl' | 'max-w-6xl' | 'max-w-7xl' | 'max-w-full';
  children: ReactNode;
}

/**
 * Standard chrome for every destination reachable from the Admin Control Center.
 *
 * It exists so the hub and the pages it links to are the same visual surface:
 * one `h1`, one back link to `/admin`, one actions slot, and the Control
 * Center's band spacing. Admin pages should use this instead of `PageTemplate`
 * directly so the guarantees live in one place.
 *
 * @example
 * ```tsx
 * <AdminPageShell
 *   title="Users"
 *   subtitle="Accounts, roles, permissions, and authentication history."
 *   icon={UsersIcon}
 *   actions={<Button variant="primary">Add user</Button>}
 * >
 *   <AdminSection caption="Accounts">…</AdminSection>
 * </AdminPageShell>
 * ```
 */
export function AdminPageShell({
  title,
  subtitle,
  icon,
  actions,
  titleActions,
  parent,
  embedded = false,
  maxWidth = 'max-w-7xl',
  children,
}: AdminPageShellProps) {
  return (
    <PageTemplate
      title={title}
      subtitle={subtitle}
      icon={icon}
      actions={actions}
      titleActions={titleActions}
      parent={parent === null ? undefined : (parent ?? ADMIN_HUB_PARENT)}
      embedded={embedded}
      maxWidth={maxWidth}
    >
      <div className="flex flex-col gap-8">{children}</div>
    </PageTemplate>
  );
}
