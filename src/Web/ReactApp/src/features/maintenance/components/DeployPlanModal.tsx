/**
 * DeployPlanModal Component
 *
 * Allows deploying a maintenance plan to a printer and viewing/removing
 * existing deployments.
 */

import React, { useMemo, useState } from 'react';
import { toast } from 'sonner';
import { format } from 'date-fns';
import { Badge, Button } from '@/common/components/ui';
import { Input } from '@/common/components/ui/Input';
import { Select } from '@/common/components/ui/Select';
import { Modal } from '@/common/components/modals/Modal';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import { DeleteIcon } from '@/common/components/icons/MdiIcons';
import { usePrinters } from '@/common/hooks/useApi';
import {
  useScheduleDeployments,
  useDeployPlan,
  useDeleteScheduleDeployment,
} from '../hooks/useScheduleDeployments';
import type {
  MaintenancePlanDto,
  PrinterMaintenanceScheduleDto,
} from '@/types/maintenance';

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
  const [notes, setNotes] = useState('');
  const [undeploying, setUndeploying] = useState<PrinterMaintenanceScheduleDto | null>(null);

  // Filter out printers already deployed
  const deployedPrinterIds = useMemo(
    () => new Set(deployments.map(d => d.printerId)),
    [deployments],
  );

  const availablePrinters = useMemo(
    () => printers.filter(p => !deployedPrinterIds.has(p.id)),
    [printers, deployedPrinterIds],
  );

  React.useEffect(() => {
    if (isOpen) {
      setSelectedPrinterId('');
      setNotes('');
    }
  }, [isOpen]);

  const handleDeploy = async () => {
    if (!plan || !selectedPrinterId) return;
    try {
      await deployMutation.mutateAsync({
        maintenancePlanId: plan.id,
        printerId: selectedPrinterId,
        notes: notes.trim() || null,
      });
      const printer = printers.find(p => p.id === selectedPrinterId);
      toast.success(`Deployed to ${printer?.name ?? 'printer'}`);
      setSelectedPrinterId('');
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
              disabled={!selectedPrinterId || deployMutation.isPending}
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
                      <span className="text-sm font-medium text-pf-text-primary">{d.printerName ?? 'Unknown'}</span>
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
                      aria-label={`Undeploy from ${d.printerName}`}
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
        message={`Remove this plan from "${undeploying?.printerName ?? 'printer'}"? Maintenance tracking will stop.`}
        confirmButtonText="Undeploy"
        isDangerous
        onConfirm={handleUndeploy}
        onCancel={() => setUndeploying(null)}
      />
    </>
  );
}
