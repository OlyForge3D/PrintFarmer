/**
 * FallbackGroupsPanel (issue #718).
 *
 * Renders the printer's configured filament fallback chains inside the
 * multi-toolhead sidebar. Presents chain state per-member, offers CRUD +
 * reorder controls, and coexists with the existing `ToolheadSpoolPicker`
 * (which retains ownership of assign/clear/MMU actions).
 */
import { useMemo, useState } from "react";
import {
  Alert,
  Badge,
  Button,
  Spinner,
} from "@/common/components/ui";
import {
  PlusIcon,
  EditIcon,
  DeleteIcon,
  AlertCircleIcon,
} from "@/common/components/icons/MdiIcons";
import { ConfirmationModal } from "@/common/components/modals/ConfirmationModal";
import { useFilamentTypes } from "@/common/hooks/useApi";
import { usePrinterFilamentCoverage } from "@/features/filament-coverage/hooks";
import type { ToolheadDto } from "@/types/api";
import { ToolheadType } from "@/types/api";
import {
  getErrorMessage,
  getErrorStatus,
  isFeatureDisabledError,
} from "@/features/parts-inventory/utils/problemDetails";
import {
  useFallbackGroups,
  useCreateFallbackGroup,
  useUpdateFallbackGroup,
  useDeleteFallbackGroup,
  useReorderFallbackGroupMembers,
} from "@/features/fallback-groups/hooks";
import {
  buildCoverageLookup,
  deriveFallbackGroupChainState,
  type FilamentFallbackGroup,
} from "@/features/fallback-groups/types";
import { FallbackChainDisplay } from "./FallbackChainDisplay";
import { FallbackGroupEditor } from "./FallbackGroupEditor";

interface FallbackGroupsPanelProps {
  printerId: string;
  toolheads: ToolheadDto[];
}

function isPhysical(toolhead: ToolheadDto): boolean {
  const t = toolhead.toolheadType;
  // Backend serializes enums as strings but legacy captures may still send
  // the numeric value; accept both. When the field is missing entirely we
  // treat the toolhead as physical to preserve pre-#711 behavior.
  if (t == null) return true;
  if (typeof t === "string") return t === "Physical";
  return t === ToolheadType.Physical;
}

export function FallbackGroupsPanel({
  printerId,
  toolheads,
}: FallbackGroupsPanelProps) {
  const physicalToolheads = useMemo(
    () => toolheads.filter(isPhysical),
    [toolheads],
  );

  const groupsQuery = useFallbackGroups(printerId);
  const coverageQuery = usePrinterFilamentCoverage(printerId);
  const filamentTypesQuery = useFilamentTypes();

  const createMutation = useCreateFallbackGroup(printerId);
  const updateMutation = useUpdateFallbackGroup(printerId);
  const deleteMutation = useDeleteFallbackGroup(printerId);
  const reorder = useReorderFallbackGroupMembers(printerId);

  const [editorOpen, setEditorOpen] = useState(false);
  const [editingGroup, setEditingGroup] = useState<FilamentFallbackGroup | null>(null);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [pendingDelete, setPendingDelete] = useState<FilamentFallbackGroup | null>(null);
  const [reorderError, setReorderError] = useState<string | null>(null);

  const coverageLookup = useMemo(
    () => buildCoverageLookup(coverageQuery.data?.toolheads ?? null),
    [coverageQuery.data?.toolheads],
  );

  const materialSuggestions = useMemo(
    () => (filamentTypesQuery.data ?? []).map((t) => t.name),
    [filamentTypesQuery.data],
  );

  const groups = groupsQuery.data ?? [];

  const openCreate = () => {
    setEditingGroup(null);
    setSubmitError(null);
    setEditorOpen(true);
  };

  const openEdit = (group: FilamentFallbackGroup) => {
    setEditingGroup(group);
    setSubmitError(null);
    setEditorOpen(true);
  };

  // Backend requires the `farm_admin` role for POST/PUT/DELETE and returns
  // 403 for non-admin users. `getErrorMessage` will pass through any server
  // detail if present, but for a bare 403 we surface an actionable message.
  const describeMutationError = (err: unknown, fallback: string): string => {
    if (getErrorStatus(err) === 403) {
      return "Admin role required to configure fallback chains.";
    }
    return getErrorMessage(err, fallback);
  };

  const handleSubmit = async (request: {
    name: string;
    materialType: string;
    displayOrder?: number;
    toolheadIds: string[];
  }) => {
    setSubmitError(null);
    try {
      if (editingGroup) {
        await updateMutation.mutateAsync({ groupId: editingGroup.id, request });
      } else {
        await createMutation.mutateAsync(request);
      }
      setEditorOpen(false);
      setEditingGroup(null);
    } catch (err) {
      setSubmitError(describeMutationError(err, "Failed to save fallback chain"));
    }
  };

  const handleConfirmDelete = async () => {
    if (!pendingDelete) return;
    try {
      await deleteMutation.mutateAsync(pendingDelete.id);
      setPendingDelete(null);
    } catch (err) {
      setSubmitError(describeMutationError(err, "Failed to delete fallback chain"));
      setPendingDelete(null);
    }
  };

  const handleReorder = async (group: FilamentFallbackGroup, fromIndex: number, toIndex: number) => {
    if (fromIndex === toIndex) return;
    if (toIndex < 0 || toIndex >= group.members.length) return;
    setReorderError(null);
    const nextIds = group.members.map((m) => m.toolheadId);
    const [moved] = nextIds.splice(fromIndex, 1);
    nextIds.splice(toIndex, 0, moved);
    try {
      await reorder.reorder(group, nextIds);
    } catch (err) {
      setReorderError(describeMutationError(err, "Failed to reorder chain"));
    }
  };

  // Gate: requires ≥2 physical toolheads. Rendered by parent as well, but we
  // also defend here so the component is safe to drop anywhere.
  if (physicalToolheads.length < 2) {
    return null;
  }

  // Operator feature gate: backend returns 404 with ProblemDetails
  // `code: "featureDisabled"` when the "multi-slot fallback" feature is
  // switched off. Hide the panel silently in that case — surfacing an error
  // would be noise since the operator has intentionally disabled it.
  if (groupsQuery.isError && isFeatureDisabledError(groupsQuery.error)) {
    return null;
  }

  return (
    <div
      className="mt-3 rounded border border-pf-border/60 bg-pf-bg-1 p-3"
      data-testid="fallback-groups-panel"
      aria-labelledby="fallback-groups-panel-heading"
    >
      <div className="flex items-center justify-between gap-2 mb-2">
        <h4
          id="fallback-groups-panel-heading"
          className="text-sm font-semibold text-pf-text-primary"
        >
          Fallback chains
        </h4>
        <Button
          size="sm"
          variant="secondary"
          onClick={openCreate}
          disabled={createMutation.isPending}
          aria-label="Add a new fallback chain"
        >
          <span className="inline-flex items-center gap-1">
            <PlusIcon className="h-4 w-4" ariaLabel="" />
            New chain
          </span>
        </Button>
      </div>

      {groupsQuery.isLoading && (
        <div className="flex items-center gap-2 text-xs text-pf-text-secondary" role="status">
          <Spinner size="sm" />
          Loading fallback chains…
        </div>
      )}

      {groupsQuery.isError && (
        <Alert type="error" title="Couldn't load fallback chains">
          {getErrorMessage(groupsQuery.error, "Failed to load fallback chains")}
        </Alert>
      )}

      {reorderError && (
        <Alert type="error" title="Reorder failed" className="mb-2">
          {reorderError}
        </Alert>
      )}

      {!groupsQuery.isLoading && !groupsQuery.isError && groups.length === 0 && (
        <div className="rounded border border-dashed border-pf-border bg-pf-bg-0 p-3 text-xs text-pf-text-secondary">
          <p className="mb-1 font-medium text-pf-text-primary">
            No fallback chains configured.
          </p>
          <p>
            Group two or more physical toolheads to have the printer automatically
            switch to a backup spool of the same material when the primary runs out.
          </p>
        </div>
      )}

      <ul className="space-y-3" aria-label="Configured fallback chains">
        {groups.map((group) => {
          const chain = deriveFallbackGroupChainState(group, coverageLookup);
          const isBusy =
            (updateMutation.isPending && editingGroup?.id === group.id) ||
            (deleteMutation.isPending && pendingDelete?.id === group.id) ||
            reorder.isPending;
          return (
            <li
              key={group.id}
              data-testid={`fallback-group-${group.id}`}
              className="rounded border border-pf-border/60 bg-pf-bg-0 p-2.5"
            >
              <div className="flex items-start justify-between gap-2 mb-1.5">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="text-sm font-semibold text-pf-text-primary">
                    {group.name}
                  </span>
                  <Badge variant="primary" size="sm">{group.materialType}</Badge>
                  <span className="text-xs text-pf-text-tertiary">
                    {group.members.length} toolhead{group.members.length === 1 ? "" : "s"}
                  </span>
                </div>
                <div className="flex items-center gap-1">
                  <Button
                    size="sm"
                    variant="subtle"
                    onClick={() => openEdit(group)}
                    disabled={isBusy}
                    aria-label={`Edit fallback chain ${group.name}`}
                  >
                    <EditIcon className="h-4 w-4" ariaLabel="" />
                  </Button>
                  <Button
                    size="sm"
                    variant="subtle"
                    onClick={() => setPendingDelete(group)}
                    disabled={isBusy}
                    aria-label={`Delete fallback chain ${group.name}`}
                  >
                    <DeleteIcon className="h-4 w-4" ariaLabel="" />
                  </Button>
                </div>
              </div>

              {chain.mixedMaterialWarning && (
                <Alert type="warning" title="Mixed materials in chain" className="mb-2">
                  <span className="inline-flex items-center gap-1 text-xs">
                    <AlertCircleIcon className="h-3.5 w-3.5" ariaLabel="Warning" />
                    A member's loaded spool material differs from
                    <span className="font-medium"> {group.materialType}</span>.
                  </span>
                </Alert>
              )}

              <FallbackChainDisplay
                chain={chain}
                onMoveUp={(index) => handleReorder(group, index, index - 1)}
                onMoveDown={(index) => handleReorder(group, index, index + 1)}
                disabled={isBusy}
              />
            </li>
          );
        })}
      </ul>

      <FallbackGroupEditor
        // Force remount whenever the editor opens for a different group so
        // the internal draft state is re-initialized from the target group
        // without relying on a setState-in-effect reset.
        key={editorOpen ? `editor:${editingGroup?.id ?? "new"}` : "editor:closed"}
        isOpen={editorOpen}
        group={editingGroup ?? undefined}
        physicalToolheads={physicalToolheads}
        existingGroups={groups}
        materialSuggestions={materialSuggestions}
        onClose={() => {
          setEditorOpen(false);
          setEditingGroup(null);
          setSubmitError(null);
        }}
        onSubmit={handleSubmit}
        submitError={submitError}
        isSubmitting={createMutation.isPending || updateMutation.isPending}
      />

      <ConfirmationModal
        isOpen={pendingDelete !== null}
        title="Delete fallback chain?"
        message={
          pendingDelete
            ? `"${pendingDelete.name}" will be removed. Loaded spools are not affected.`
            : ""
        }
        confirmButtonText="Delete chain"
        cancelButtonText="Cancel"
        isDangerous
        isConfirming={deleteMutation.isPending}
        onConfirm={handleConfirmDelete}
        onCancel={() => setPendingDelete(null)}
      />
    </div>
  );
}
