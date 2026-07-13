/**
 * DeployPlanModal Component
 *
 * Allows deploying a maintenance plan to a printer and viewing/removing
 * existing deployments.
 */

import React, { useMemo, useState } from 'react';
import { toast } from 'sonner';
import { format } from 'date-fns';
import { useQuery } from '@tanstack/react-query';
import { Badge, Button } from '@/common/components/ui';
import { Input } from '@/common/components/ui/Input';
import { Select } from '@/common/components/ui/Select';
import { Modal } from '@/common/components/modals/Modal';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import { DeleteIcon } from '@/common/components/icons/MdiIcons';
import { usePrinters } from '@/common/hooks/useApi';
import { apiClient } from '@/services/api';
import {
  useScheduleDeployments,
  useDeployPlan,
  useDeleteScheduleDeployment,
} from '../hooks/useScheduleDeployments';
import { ToolheadScopePicker } from './ToolheadScopePicker';
import {
  PRINTER_WIDE_SCOPE,
  toolheadIdFromScope,
  type ToolheadScopeValue,
} from './toolheadScope';
import { isEligibleMaintenanceToolhead } from '@/features/printers/utils/isEligibleMaintenanceToolhead';
import type {
  MaintenancePlanDto,
  PrinterMaintenanceScheduleDto,
} from '@/types/maintenance';
import type { PrinterDetails } from '@/types/api';

interface DeployPlanModalProps {
  isOpen: boolean;
  plan: MaintenancePlanDto | null;
  onClose: () => void;
}

export function DeployPlanModal({ isOpen, plan, onClose }: DeployPlanModalProps) {
  const { data: printers = [] } = usePrinters();
  const { data: deployments = [] } = useScheduleDeployments(undefined, plan?.id);
  const deployMutation = useDeployPlan();
  const undeployMutation = useDeleteScheduleDeployment();

  const [selectedPrinterId, setSelectedPrinterId] = useState('');
  const [scope, setScope] = useState<ToolheadScopeValue>(PRINTER_WIDE_SCOPE);
  const [notes, setNotes] = useState('');
  const [undeploying, setUndeploying] = useState<PrinterMaintenanceScheduleDto | null>(null);

  // Fetch per-tool capability + toolhead list for the printer under the
  // picker. Only run when a printer is chosen and the modal is open.
  //
  // Consuming `status`/`isLoading`/`isError` alongside `data` is critical: a
  // per-tool-capable printer will start with `printerDetails === undefined`
  // during the first fetch, and without a load gate on Deploy the modal
  // would silently send `toolheadId: null` for a printer whose scope picker
  // simply hadn't rendered yet. `refetch` powers the accessible Retry
  // button below.
  const {
    data: printerDetails,
    isLoading: printerDetailsLoading,
    isError: printerDetailsIsError,
    error: printerDetailsError,
    refetch: refetchPrinterDetails,
  } = useQuery<PrinterDetails>({
    queryKey: ['printerDetails', selectedPrinterId],
    queryFn: () => apiClient.getPrinterDetails(selectedPrinterId),
    enabled: isOpen && !!selectedPrinterId,
    staleTime: 60_000,
  });

  // Explicit three-state gate over the details query. Each state has a
  // matching UI (loading indicator / retry banner / picker or hidden) and
  // `canDeploy` requires `detailsReady`.
  const hasPrinterSelected = !!selectedPrinterId;
  const detailsPending = hasPrinterSelected && printerDetailsLoading;
  const detailsFailed = hasPrinterSelected && printerDetailsIsError;
  const detailsReady =
    hasPrinterSelected && !printerDetailsLoading && !printerDetailsIsError && !!printerDetails;

  const perToolAllowed = detailsReady && printerDetails?.supportsPerToolAttribution === true;
  const eligibleToolheads = useMemo(
    () => (printerDetails?.toolheads ?? []).filter(isEligibleMaintenanceToolhead),
    [printerDetails],
  );
  const showScopePicker = perToolAllowed && eligibleToolheads.length >= 2;

  // Scope-aware duplicate filter. A printer may host multiple deployments of
  // the same plan as long as each targets a distinct `(printerId, toolheadId)`
  // pair (e.g. printer-wide + one per physical tool). We compute the set of
  // (printerId, toolheadId | 'printer-wide') tuples already deployed so the
  // caller can only choose a printer that still has *some* available scope,
  // and the scope picker below filters out the specific tools that are taken.
  const deployedScopes = useMemo(
    () =>
      new Set(
        deployments.map(
          d => `${d.printerId}:${d.toolheadId ?? PRINTER_WIDE_SCOPE}`
        )
      ),
    [deployments],
  );

  const availablePrinters = useMemo(() => {
    // We conservatively surface every printer where any deployment slot may
    // still be available. Determining "no slots left" for a per-tool printer
    // requires that printer's details (which we only load after selection),
    // so we let the scope picker below gate individual (printerId, toolheadId)
    // slots and disable the Deploy button when the current selection is taken.
    // A printer whose printer-wide slot is taken and has no per-tool support
    // is left in the list; the picker will not appear and the Deploy button
    // will remain disabled because `printerWideAvailable` is false.
    return printers;
  }, [printers]);

  const availableScopeToolheads = useMemo(
    () =>
      eligibleToolheads.filter(
        t => !deployedScopes.has(`${selectedPrinterId}:${t.id}`)
      ),
    [eligibleToolheads, deployedScopes, selectedPrinterId],
  );
  const printerWideAvailable =
    !!selectedPrinterId &&
    !deployedScopes.has(`${selectedPrinterId}:${PRINTER_WIDE_SCOPE}`);

  React.useEffect(() => {
    if (isOpen) {
      setSelectedPrinterId('');
      setScope(PRINTER_WIDE_SCOPE);
      setNotes('');
    }
  }, [isOpen]);

  // If the current scope becomes unavailable (e.g. printer-wide already
  // deployed, or a picked toolhead already deployed), reset to a valid one.
  React.useEffect(() => {
    if (!selectedPrinterId) return;
    if (scope === PRINTER_WIDE_SCOPE && !printerWideAvailable) {
      const first = availableScopeToolheads[0];
      if (first) setScope(first.id);
      return;
    }
    if (scope !== PRINTER_WIDE_SCOPE) {
      const stillAvailable = availableScopeToolheads.some(t => t.id === scope);
      if (!stillAvailable) {
        setScope(printerWideAvailable ? PRINTER_WIDE_SCOPE : (availableScopeToolheads[0]?.id ?? PRINTER_WIDE_SCOPE));
      }
    }
  }, [selectedPrinterId, scope, printerWideAvailable, availableScopeToolheads]);

  const handleDeploy = async () => {
    if (!plan || !selectedPrinterId) return;
    // Defence in depth. Even though `canDeploy` disables the button while
    // `detailsReady` is false, an assistive-tech or programmatic click on a
    // disabled control must not send a stale `toolheadId: null` for a
    // printer whose capability we haven't confirmed yet.
    if (!detailsReady) return;
    const toolheadId = showScopePicker ? toolheadIdFromScope(scope) : null;
    try {
      await deployMutation.mutateAsync({
        maintenancePlanId: plan.id,
        printerId: selectedPrinterId,
        toolheadId,
        notes: notes.trim() || null,
      });
      const printer = printers.find(p => p.id === selectedPrinterId);
      const toolheadName =
        toolheadId != null
          ? eligibleToolheads.find(t => t.id === toolheadId)?.name ?? 'toolhead'
          : null;
      toast.success(
        toolheadName
          ? `Deployed to ${printer?.name ?? 'printer'} (${toolheadName})`
          : `Deployed to ${printer?.name ?? 'printer'}`
      );
      setSelectedPrinterId('');
      setScope(PRINTER_WIDE_SCOPE);
      setNotes('');
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Failed to deploy plan');
    }
  };

  const handleUndeploy = async () => {
    if (!undeploying) return;
    try {
      await undeployMutation.mutateAsync(undeploying.id);
      toast.success(`Undeployed from ${undeploying.printerName ?? 'printer'}`);
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Failed to undeploy');
    }
    setUndeploying(null);
  };

  const canDeploy =
    !!selectedPrinterId &&
    !deployMutation.isPending &&
    // Details must be fully resolved. This is the guard that prevents a
    // per-tool-capable printer from being silently deployed as printer-wide
    // during the capability-query race window (loading OR errored).
    detailsReady &&
    (showScopePicker
      ? scope === PRINTER_WIDE_SCOPE
        ? printerWideAvailable
        : availableScopeToolheads.some(t => t.id === scope)
      : printerWideAvailable);

  if (!plan) return null;

  return (
    <>
      <Modal isOpen={isOpen} onClose={onClose} title={`Deploy: ${plan.name}`} size="lg">
        <div className="space-y-5">
          {/* Deploy to new printer */}
          <section className="space-y-3">
            <h4 className="text-sm font-medium text-pf-text-secondary">Deploy to Printer</h4>
            <Select
              value={selectedPrinterId}
              onChange={(e) => {
                // Reset scope synchronously on every printer change. A stale
                // toolhead id from the previous printer would otherwise be
                // carried across (the effect below runs after render, so the
                // Deploy button briefly sees an invalid scope). Setting the
                // scope back to PRINTER_WIDE_SCOPE at the same tick as
                // `selectedPrinterId` closes that window.
                setSelectedPrinterId(e.target.value);
                setScope(PRINTER_WIDE_SCOPE);
              }}
              aria-label="Select printer to deploy to"
            >
              <option value="">Select printer…</option>
              {availablePrinters.map(p => (
                <option key={p.id} value={p.id}>{p.name}</option>
              ))}
            </Select>
            {/* Details query — three states.
                Loading: role="status" gives the visible text an implicit
                aria-live="polite" and aria-atomic="true" so screen readers
                announce the pending capability check without stealing
                focus.
                Error: role="alert" for assertive announcement plus a Retry
                action wired to the query's own `refetch` (same key, so
                react-query re-uses the mutation-observer wiring).
                Ready: the picker or absence of picker is what the operator
                sees. */}
            {detailsPending && (
              <p
                role="status"
                className="text-xs text-pf-text-tertiary"
                data-testid="printer-details-loading"
              >
                Loading printer capabilities…
              </p>
            )}
            {detailsFailed && (
              <div
                role="alert"
                className="flex items-center gap-2 rounded-md border border-pf-error/40 bg-pf-error/10 px-3 py-2 text-xs text-pf-error"
                data-testid="printer-details-error"
              >
                <span className="flex-1">
                  Could not load printer capabilities
                  {printerDetailsError instanceof Error && printerDetailsError.message
                    ? `: ${printerDetailsError.message}`
                    : '.'}
                </span>
                <Button
                  type="button"
                  variant="subtle"
                  size="sm"
                  onClick={() => { void refetchPrinterDetails(); }}
                  aria-label="Retry loading printer capabilities"
                >
                  Retry
                </Button>
              </div>
            )}
            {selectedPrinterId && showScopePicker && (
              <ToolheadScopePicker
                value={scope}
                onChange={setScope}
                toolheads={eligibleToolheads}
                helperText={
                  !printerWideAvailable && availableScopeToolheads.length === 0
                    ? 'All scopes for this printer are already deployed.'
                    : undefined
                }
              />
            )}
            {selectedPrinterId && (
              <Input
                value={notes}
                onChange={(e) => setNotes(e.target.value)}
                placeholder="Deployment notes (optional)"
                maxLength={500}
              />
            )}
            <Button
              variant="primary"
              size="sm"
              onClick={handleDeploy}
              disabled={!canDeploy}
              loading={deployMutation.isPending}
            >
              Deploy
            </Button>
          </section>

          {/* Current deployments */}
          <section className="space-y-2">
            <h4 className="text-sm font-medium text-pf-text-secondary">
              Current Deployments ({deployments.length})
            </h4>
            {deployments.length === 0 ? (
              <p className="text-xs text-pf-text-tertiary py-2">Not deployed to any printers yet.</p>
            ) : (
              <div className="divide-y divide-pf-border rounded-lg border border-pf-border">
                {deployments.map(d => (
                  <div key={d.id} className="flex items-center gap-3 p-3">
                    <div className="flex-1 min-w-0">
                      <div className="flex items-center gap-2 flex-wrap">
                        <span className="text-sm font-medium text-pf-text-primary">{d.printerName ?? 'Unknown'}</span>
                        {d.toolheadId != null && d.toolheadName && (
                          <Badge variant="default" className="text-[10px]">
                            {d.toolheadName}
                          </Badge>
                        )}
                      </div>
                      <div className="flex items-center gap-2 mt-0.5 text-xs text-pf-text-tertiary">
                        <span>Deployed {format(new Date(d.deployedAt), 'MMM d, yyyy')}</span>
                        {!d.isActive && <Badge variant="default" className="text-[10px]">Paused</Badge>}
                      </div>
                      {d.notes && (
                        <p className="text-xs text-pf-text-tertiary mt-0.5 line-clamp-1">{d.notes}</p>
                      )}
                    </div>
                    <Button
                      variant="subtle"
                      size="sm"
                      onClick={() => setUndeploying(d)}
                      aria-label={`Undeploy from ${d.printerName}${d.toolheadName ? ` (${d.toolheadName})` : ''}`}
                      className="hover:text-pf-error shrink-0"
                    >
                      <DeleteIcon className="h-4 w-4" />
                    </Button>
                  </div>
                ))}
              </div>
            )}
          </section>
        </div>
      </Modal>

      <ConfirmationModal
        isOpen={!!undeploying}
        title="Undeploy Plan"
        message={`Remove this plan from "${undeploying?.printerName ?? 'printer'}"${undeploying?.toolheadName ? ` (${undeploying.toolheadName})` : ''}? Maintenance tracking will stop.`}
        confirmButtonText="Undeploy"
        isDangerous
        onConfirm={handleUndeploy}
        onCancel={() => setUndeploying(null)}
      />
    </>
  );
}
