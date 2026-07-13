/**
 * FallbackGroupEditor (issue #718).
 *
 * Modal form for create/edit of a filament fallback group. Preserves the
 * existing UI patterns (Modal + Button/Input from the shared UI library) and
 * mirrors the backend validation rules (`validateFallbackGroupDraft`) so
 * common problems surface inline before a network round-trip.
 *
 * Toolhead ordering is managed through explicit Add/Remove + Move up/Move
 * down controls so the editor is fully keyboard accessible (no drag/drop
 * dependency, and no color-only affordance).
 */
import { useMemo, useState } from "react";
import { Button, Input, Label, Alert, Badge, Select } from "@/common/components/ui";
import { Modal } from "@/common/components/modals/Modal";
import {
  ArrowUpIcon,
  ArrowDownIcon,
  PlusIcon,
  DeleteIcon,
  AlertCircleIcon,
} from "@/common/components/icons/MdiIcons";
import type { ToolheadDto } from "@/types/api";
import {
  validateFallbackGroupDraft,
  type CreateFilamentFallbackGroupRequest,
  type FallbackGroupValidationError,
  type FilamentFallbackGroup,
} from "@/features/fallback-groups/types";

interface FallbackGroupEditorProps {
  isOpen: boolean;
  /** When present, we're editing this group; otherwise it's a create. */
  group?: FilamentFallbackGroup;
  /** All physical toolheads on the printer (MMU gates filtered out by caller). */
  physicalToolheads: ToolheadDto[];
  /** Every existing group for this printer, used for name-uniqueness validation. */
  existingGroups: readonly FilamentFallbackGroup[];
  /** Known filament material types (from `useFilamentTypes`). Optional — free entry allowed. */
  materialSuggestions?: string[];
  onClose: () => void;
  onSubmit: (request: CreateFilamentFallbackGroupRequest) => Promise<void>;
  /** Server-side error (ProblemDetails detail) shown at the top of the form. */
  submitError?: string | null;
  isSubmitting?: boolean;
}

interface DraftState {
  name: string;
  materialType: string;
  toolheadIds: string[];
}

function initialDraft(group: FilamentFallbackGroup | undefined): DraftState {
  if (!group) return { name: "", materialType: "", toolheadIds: [] };
  return {
    name: group.name,
    materialType: group.materialType,
    toolheadIds: group.members.map((m) => m.toolheadId),
  };
}

function toolheadLabel(toolhead: ToolheadDto): string {
  const name = toolhead.name?.trim();
  return name ? `T${toolhead.index} — ${name}` : `T${toolhead.index}`;
}

export function FallbackGroupEditor({
  isOpen,
  group,
  physicalToolheads,
  existingGroups,
  materialSuggestions = [],
  onClose,
  onSubmit,
  submitError,
  isSubmitting = false,
}: FallbackGroupEditorProps) {
  // Draft state is initialized from `group` at mount time. The parent
  // component remounts this editor (via `key`) whenever the target group
  // changes, so we can safely rely on the initializer here — no effect
  // needed to reset state on prop changes.
  const [draft, setDraft] = useState<DraftState>(() => initialDraft(group));
  const [clientErrors, setClientErrors] = useState<FallbackGroupValidationError[]>([]);
  const [showValidation, setShowValidation] = useState(false);

  const physicalToolheadIds = useMemo(
    () => new Set(physicalToolheads.map((t) => t.id)),
    [physicalToolheads],
  );

  const availableToolheads = useMemo(
    () => physicalToolheads.filter((t) => !draft.toolheadIds.includes(t.id)),
    [physicalToolheads, draft.toolheadIds],
  );

  const chainToolheads = useMemo(() => {
    return draft.toolheadIds
      .map((id) => physicalToolheads.find((t) => t.id === id))
      .filter((t): t is ToolheadDto => t !== undefined);
  }, [draft.toolheadIds, physicalToolheads]);

  // User-chosen add-target id, if any. When the pick is no longer available
  // (because the user just added it), the effective add id falls back to the
  // first remaining option — derived during render so we avoid the
  // setState-in-effect anti-pattern flagged by react-hooks/set-state-in-effect.
  const [selectedAddId, setSelectedAddId] = useState<string>("");
  const pendingAddId =
    selectedAddId && availableToolheads.some((t) => t.id === selectedAddId)
      ? selectedAddId
      : availableToolheads[0]?.id ?? "";

  const errorsByField = useMemo(() => {
    const map = new Map<FallbackGroupValidationError["field"], string>();
    for (const err of clientErrors) {
      if (!map.has(err.field)) map.set(err.field, err.message);
    }
    return map;
  }, [clientErrors]);

  const runValidation = (next: DraftState): FallbackGroupValidationError[] => {
    return validateFallbackGroupDraft(
      {
        name: next.name,
        materialType: next.materialType,
        toolheadIds: next.toolheadIds,
      },
      existingGroups,
      physicalToolheadIds,
      group?.id,
    );
  };

  const handleAdd = () => {
    if (!pendingAddId) return;
    setDraft((prev) => ({ ...prev, toolheadIds: [...prev.toolheadIds, pendingAddId] }));
  };

  const handleRemove = (index: number) => {
    setDraft((prev) => ({
      ...prev,
      toolheadIds: prev.toolheadIds.filter((_, i) => i !== index),
    }));
  };

  const handleMove = (index: number, direction: -1 | 1) => {
    setDraft((prev) => {
      const target = index + direction;
      if (target < 0 || target >= prev.toolheadIds.length) return prev;
      const next = [...prev.toolheadIds];
      const [moved] = next.splice(index, 1);
      next.splice(target, 0, moved);
      return { ...prev, toolheadIds: next };
    });
  };

  const handleSubmit = async () => {
    const nextDraft = draft;
    const errors = runValidation(nextDraft);
    setClientErrors(errors);
    setShowValidation(true);
    if (errors.length > 0) return;

    const request: CreateFilamentFallbackGroupRequest = {
      name: nextDraft.name.trim(),
      materialType: nextDraft.materialType.trim(),
      displayOrder: group?.displayOrder,
      toolheadIds: nextDraft.toolheadIds,
    };
    await onSubmit(request);
  };

  const hasFieldError = (field: FallbackGroupValidationError["field"]) =>
    showValidation && errorsByField.has(field);

  // The "mixed material" preview surface. We warn before saving when the
  // user has picked toolheads with a loaded spool material that differs from
  // the target group material — matches the acceptance criterion.
  const previewMismatch = useMemo(() => {
    const materialLower = draft.materialType.trim().toLowerCase();
    if (materialLower.length === 0) return [] as ToolheadDto[];
    return chainToolheads.filter((t) => {
      const loaded = t.currentMaterial?.trim().toLowerCase();
      return loaded && loaded !== materialLower;
    });
  }, [chainToolheads, draft.materialType]);

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={group ? `Edit ${group.name}` : "New fallback chain"}
      size="lg"
      isDisabled={isSubmitting}
      footer={
        <div className="flex gap-2 w-full justify-end">
          <Button variant="secondary" onClick={onClose} disabled={isSubmitting}>
            Cancel
          </Button>
          <Button
            variant="primary"
            onClick={handleSubmit}
            disabled={isSubmitting}
            aria-busy={isSubmitting}
          >
            {isSubmitting ? "Saving…" : group ? "Save changes" : "Create chain"}
          </Button>
        </div>
      }
    >
      <div className="space-y-4">
        {submitError && (
          <Alert type="error" title="Server rejected the chain">
            {submitError}
          </Alert>
        )}

        <div>
          <Label htmlFor="fallback-group-name">Name</Label>
          <Input
            id="fallback-group-name"
            data-autofocus
            value={draft.name}
            onChange={(e) => setDraft((prev) => ({ ...prev, name: e.target.value }))}
            invalid={hasFieldError("name")}
            aria-describedby={hasFieldError("name") ? "fallback-group-name-error" : undefined}
            disabled={isSubmitting}
            placeholder="PLA lineup"
          />
          {hasFieldError("name") && (
            <p id="fallback-group-name-error" className="mt-1 text-xs text-pf-error-text">
              {errorsByField.get("name")}
            </p>
          )}
        </div>

        <div>
          <Label htmlFor="fallback-group-material">Material</Label>
          <Input
            id="fallback-group-material"
            list="fallback-group-material-suggestions"
            value={draft.materialType}
            onChange={(e) => setDraft((prev) => ({ ...prev, materialType: e.target.value }))}
            invalid={hasFieldError("materialType")}
            aria-describedby={hasFieldError("materialType") ? "fallback-group-material-error" : undefined}
            disabled={isSubmitting}
            placeholder="PLA"
          />
          <datalist id="fallback-group-material-suggestions">
            {materialSuggestions.map((m) => (
              <option key={m} value={m} />
            ))}
          </datalist>
          {hasFieldError("materialType") && (
            <p id="fallback-group-material-error" className="mt-1 text-xs text-pf-error-text">
              {errorsByField.get("materialType")}
            </p>
          )}
        </div>

        <div>
          <div className="flex items-end justify-between gap-2">
            <div className="flex-1">
              <Label htmlFor="fallback-group-add-toolhead">Add physical toolhead</Label>
              <Select
                id="fallback-group-add-toolhead"
                value={pendingAddId}
                onChange={(e) => setSelectedAddId(e.target.value)}
                disabled={isSubmitting || availableToolheads.length === 0}
              >
                {availableToolheads.length === 0 ? (
                  <option value="">All physical toolheads added</option>
                ) : (
                  availableToolheads.map((t) => (
                    <option key={t.id} value={t.id}>
                      {toolheadLabel(t)}
                      {t.currentMaterial ? ` · loaded ${t.currentMaterial}` : " · empty"}
                    </option>
                  ))
                )}
              </Select>
            </div>
            <Button
              variant="secondary"
              onClick={handleAdd}
              disabled={isSubmitting || !pendingAddId}
              aria-label="Add selected toolhead to chain"
            >
              <span className="inline-flex items-center gap-1">
                <PlusIcon className="h-4 w-4" ariaLabel="" />
                Add
              </span>
            </Button>
          </div>
          {hasFieldError("toolheadIds") && (
            <p className="mt-1 text-xs text-pf-error-text" role="alert">
              {errorsByField.get("toolheadIds")}
            </p>
          )}
        </div>

        <div>
          <Label>Chain order (position 1 is used first)</Label>
          {chainToolheads.length === 0 ? (
            <p className="text-xs text-pf-text-tertiary italic mt-1">
              No toolheads added yet.
            </p>
          ) : (
            <ol
              className="mt-1 space-y-1"
              aria-label="Fallback chain order"
            >
              {chainToolheads.map((t, index) => (
                <li
                  key={t.id}
                  className="flex items-center gap-2 rounded border border-pf-border/60 bg-pf-bg-0 px-2 py-1.5"
                >
                  <span
                    aria-hidden="true"
                    className="inline-flex h-5 w-5 shrink-0 items-center justify-center rounded-full border border-pf-border bg-pf-bg-1 text-[10px] font-mono font-medium text-pf-text-secondary"
                  >
                    {index + 1}
                  </span>
                  <span className="flex-1 text-sm text-pf-text-primary">
                    {toolheadLabel(t)}
                  </span>
                  {t.currentMaterial && (
                    <Badge variant="default" size="sm">{t.currentMaterial}</Badge>
                  )}
                  <div
                    className="flex items-center gap-1"
                    role="group"
                    aria-label={`Reorder ${toolheadLabel(t)}`}
                  >
                    <Button
                      variant="subtle"
                      size="sm"
                      onClick={() => handleMove(index, -1)}
                      disabled={isSubmitting || index === 0}
                      aria-label={`Move ${toolheadLabel(t)} up`}
                      className="p-1! h-auto!"
                      iconCenter={<ArrowUpIcon className="h-4 w-4" ariaLabel="" />}
                    />
                    <Button
                      variant="subtle"
                      size="sm"
                      onClick={() => handleMove(index, 1)}
                      disabled={isSubmitting || index === chainToolheads.length - 1}
                      aria-label={`Move ${toolheadLabel(t)} down`}
                      className="p-1! h-auto!"
                      iconCenter={<ArrowDownIcon className="h-4 w-4" ariaLabel="" />}
                    />
                    <Button
                      variant="subtle"
                      size="sm"
                      onClick={() => handleRemove(index)}
                      disabled={isSubmitting}
                      aria-label={`Remove ${toolheadLabel(t)} from chain`}
                      className="p-1! h-auto! text-pf-error-text!"
                      iconCenter={<DeleteIcon className="h-4 w-4" ariaLabel="" />}
                    />
                  </div>
                </li>
              ))}
            </ol>
          )}
        </div>

        {previewMismatch.length > 0 && (
          <Alert type="warning" title="Mixed materials in chain">
            <div className="flex flex-col gap-1 text-xs">
              <span>
                One or more toolheads currently hold a spool whose material does not match
                <span className="font-medium"> {draft.materialType.trim() || "the chain material"}</span>.
                The backend will still validate before saving; loaded materials can be swapped later.
              </span>
              <ul className="list-disc pl-5">
                {previewMismatch.map((t) => (
                  <li key={t.id} className="inline-flex items-center gap-1">
                    <AlertCircleIcon className="h-3.5 w-3.5" ariaLabel="Material mismatch" />
                    {toolheadLabel(t)} — loaded {t.currentMaterial}
                  </li>
                ))}
              </ul>
            </div>
          </Alert>
        )}
      </div>
    </Modal>
  );
}
