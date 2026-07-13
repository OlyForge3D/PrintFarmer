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

  // Fetch per-tool capability + toolhead list for the printer under the picker.
  // Only run when a printer is chosen and the modal is open.
  const { data: printerDetails } = useQuery<PrinterDetails>({
    queryKey: ['printerDetails', selectedPrinterId],
    queryFn: () => apiClient.getPrinterDetails(selectedPrinterId),
    enabled: isOpen && !!selectedPrinterId,
    staleTime: 60_000,
  });

  const perToolAllowed = printerDetails?.supportsPerToolAttribution === true;
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
              onChange={(e) => setSelectedPrinterId(e.target.value)}
              aria-label="Select printer to deploy to"
            >
              <option value="">Select printer…</option>
              {availablePrinters.map(p => (
                <option key={p.id} value={p.id}>{p.name}</option>
              ))}
            </Select>
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
