import { AlertIcon, RefreshIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';

/** A single field comparison shown in the conflict table. */
export interface RevisionConflictField {
  /** Human-readable field name, e.g. "Name". */
  label: string;
  /** The value the current user attempted to save. */
  yourValue: string;
  /** The value currently stored on the server. */
  serverValue: string;
}

export interface RevisionConflictDialogProps {
  isOpen: boolean;
  /** Lowercase noun describing the conflicting entity, e.g. "tag" or "collection". */
  entityLabel: string;
  /** Optional entity name/identifier for the dialog title, e.g. a tag's name. */
  entityName?: string;
  /** Field-by-field diff between the user's attempted change and the current server state. */
  fields: RevisionConflictField[];
  /** True while a "reload latest" refetch is in flight. */
  isReloading?: boolean;
  /** Reloads the current server state so the user can safely reapply their change. */
  onReloadLatest: () => void;
  /** Abandons the edit entirely, discarding the attempted change. */
  onCancel: () => void;
}

/**
 * Revision-aware optimistic-concurrency conflict dialog (#844/#846). Shown when a mutation
 * is rejected with a structured HTTP 409/412 conflict because the entity was modified
 * elsewhere since it was loaded. Never silently overwrites or discards data: the user's
 * attempted values remain visible alongside the safe, current server values, and the only
 * ways forward are an explicit reload-and-retry or cancel.
 */
export function RevisionConflictDialog({
  isOpen,
  entityLabel,
  entityName,
  fields,
  isReloading = false,
  onReloadLatest,
  onCancel,
}: RevisionConflictDialogProps) {
  const title = entityName ? `Conflict updating "${entityName}"` : `Conflict updating ${entityLabel}`;

  return (
    <Modal
      isOpen={isOpen}
      onClose={onCancel}
      title={title}
      titleIcon={<AlertIcon className="w-6 h-6 text-pf-warning-text" />}
      size="md"
      isDisabled={isReloading}
      footer={
        <div className="flex gap-3 w-full">
          <Button variant="secondary" onClick={onCancel} disabled={isReloading} className="flex-1">
            Cancel
          </Button>
          <Button
            variant="primary"
            onClick={onReloadLatest}
            loading={isReloading}
            disabled={isReloading}
            iconLeft={<RefreshIcon className="w-4 h-4" />}
            className="flex-1"
          >
            Reload latest version
          </Button>
        </div>
      }
    >
      <div role="alert" className="mb-4 rounded-lg border border-pf-warning bg-pf-warning/10 p-3 text-sm text-pf-warning-text">
        This {entityLabel} was changed by someone else (or in another tab) after you started
        editing. Your changes were not saved so nothing was overwritten. Review the current
        version below, then reload it to safely reapply your changes.
      </div>

      <table className="w-full border-collapse text-sm">
        <caption className="sr-only">
          Comparison of your attempted changes and the current server version
        </caption>
        <thead>
          <tr>
            <th scope="col" className="border-b border-pf-border px-2 py-2 text-left font-medium text-pf-text-secondary">
              Field
            </th>
            <th scope="col" className="border-b border-pf-border px-2 py-2 text-left font-medium text-pf-text-secondary">
              Your change
            </th>
            <th scope="col" className="border-b border-pf-border px-2 py-2 text-left font-medium text-pf-text-secondary">
              Current version
            </th>
          </tr>
        </thead>
        <tbody>
          {fields.map((field) => (
            <tr key={field.label}>
              <th scope="row" className="border-b border-pf-border px-2 py-2 text-left font-medium text-pf-text-primary">
                {field.label}
              </th>
              <td className="border-b border-pf-border px-2 py-2 text-pf-text-secondary">
                {field.yourValue || <span className="italic text-pf-text-tertiary">(empty)</span>}
              </td>
              <td className="border-b border-pf-border px-2 py-2 text-pf-text-secondary">
                {field.serverValue || <span className="italic text-pf-text-tertiary">(empty)</span>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </Modal>
  );
}
