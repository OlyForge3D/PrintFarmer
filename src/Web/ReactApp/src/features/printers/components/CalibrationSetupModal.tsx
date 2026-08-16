import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Modal } from '@/common/components/modals/Modal';
import { Button, Alert, Checkbox, Input, Select, CollapsibleSection } from '@/common/components/ui';
import { apiClient } from '@/services/api';
import { mutationErrorMessage } from '@/common/utils/mutationError';
import type {
  CalibrationContextDto,
  CalibrationExcludedRegionDto,
  CalibrationSetupRequestDto,
  CalibrationToolheadSetupDto,
} from '@/types/api';

export interface CalibrationSetupModalProps {
  isOpen: boolean;
  onClose: () => void;
  printerId: string;
  printerName: string;
  /** Current row version (ETag) of the printer, used for optimistic concurrency. */
  rowVersion?: string | null;
}

interface ToolheadFormState {
  id: string;
  index: number;
  name: string;
  offsetX: string;
  offsetY: string;
  offsetZ: string;
  driveType: string;
  isDirectDrive: boolean;
  extruderGearRatio: string;
  maxVolumetricFlow: string;
  nozzleMaterial: string;
  nozzleIsHardened: boolean;
}

interface RegionFormState {
  name: string;
  points: { x: string; y: string }[];
}

function toolheadFromContext(t: CalibrationContextDto['toolheads'][number]): ToolheadFormState {
  return {
    id: t.id,
    index: t.index,
    name: t.name ?? `Toolhead ${t.index}`,
    offsetX: t.offset.x != null ? String(t.offset.x) : '',
    offsetY: t.offset.y != null ? String(t.offset.y) : '',
    offsetZ: t.offset.z != null ? String(t.offset.z) : '',
    driveType: t.driveType ?? '',
    isDirectDrive: t.isDirectDrive ?? false,
    extruderGearRatio: t.extruderGearRatio ?? '',
    maxVolumetricFlow: t.maxVolumetricFlow != null ? String(t.maxVolumetricFlow) : '',
    nozzleMaterial: t.nozzleMaterial ?? '',
    nozzleIsHardened: t.nozzleIsHardened ?? false,
  };
}

function regionsFromContext(regions: CalibrationExcludedRegionDto[]): RegionFormState[] {
  return regions.map((r) => ({
    name: r.name ?? '',
    points: r.polygon.map((p) => ({ x: String(p.x), y: String(p.y) })),
  }));
}

function toNullableNumber(value: string): number | null {
  const trimmed = value.trim();
  if (trimmed === '') return null;
  const parsed = Number(trimmed);
  return Number.isFinite(parsed) ? parsed : null;
}

/**
 * Operator surface for the residual calibration-eligibility fields that
 * remain manual after profile-owned sourcing (issue #1616, PR-3): per-toolhead
 * metrology, the hardware sign-off, excludedRegions (explicit `[]` supported),
 * activeToolheadIndex, capability flags, and a confirm-only firmware-verified
 * control. Firmware family/version/gcodeDialect are display-only — this
 * modal never renders an editor for those detected facts (AC #3).
 */
export function CalibrationSetupModal({ isOpen, onClose, printerId, printerName, rowVersion }: CalibrationSetupModalProps) {
  const queryClient = useQueryClient();
  const calibrationContextQueryKey = ['calibration-context', printerId] as const;

  const contextQuery = useQuery({
    queryKey: calibrationContextQueryKey,
    queryFn: () => apiClient.getCalibrationContext(printerId),
    enabled: isOpen,
  });

  const [activeToolheadIndex, setActiveToolheadIndex] = useState<number | null>(null);
  const [supportsPressureAdvance, setSupportsPressureAdvance] = useState(false);
  const [supportsFirmwareRetraction, setSupportsFirmwareRetraction] = useState(false);
  const [hardwareVerifiedAtUtc, setHardwareVerifiedAtUtc] = useState<string | null>(null);
  const [regions, setRegions] = useState<RegionFormState[]>([]);
  const [toolheads, setToolheads] = useState<ToolheadFormState[]>([]);
  const [latestRowVersion, setLatestRowVersion] = useState<string | null>(rowVersion ?? null);

  // The form state below is seeded from the query result once it resolves.
  // We adjust state during rendering (comparing against the last-synced data
  // reference) rather than in a useEffect, per React's documented pattern for
  // syncing state from a prop/query without an extra render-then-effect pass
  // (https://react.dev/learn/you-might-not-need-an-effect#adjusting-some-state-when-a-prop-changes).
  const [syncedData, setSyncedData] = useState<CalibrationContextDto | null>(null);
  if (contextQuery.data && contextQuery.data !== syncedData) {
    const data = contextQuery.data;
    setSyncedData(data);
    setActiveToolheadIndex(data.activeToolheadIndex ?? null);
    setSupportsPressureAdvance(data.supportsPressureAdvance ?? false);
    setSupportsFirmwareRetraction(data.supportsFirmwareRetraction ?? false);
    setHardwareVerifiedAtUtc(data.calibrationHardwareVerifiedAtUtc ?? null);
    setRegions(regionsFromContext(data.excludedRegions ?? []));
    setToolheads((data.toolheads ?? []).map(toolheadFromContext));
  }

  if (rowVersion && rowVersion !== latestRowVersion) {
    setLatestRowVersion(rowVersion);
  }

  const applyResult = (rv: string | null | undefined) => {
    queryClient.invalidateQueries({ queryKey: calibrationContextQueryKey });
    queryClient.invalidateQueries({ queryKey: ['printers'] });
    if (rv) setLatestRowVersion(rv);
  };

  const setupMutation = useMutation({
    mutationFn: (request: CalibrationSetupRequestDto) => {
      if (!latestRowVersion) {
        throw new Error('Printer revision unavailable. Refresh and try again.');
      }
      return apiClient.updateCalibrationSetup(printerId, request, latestRowVersion);
    },
    onSuccess: (result) => {
      applyResult(result.rowVersion);
      toast.success('Calibration setup saved');
    },
    onError: (error: unknown) => {
      toast.error(mutationErrorMessage(error, 'Failed to save calibration setup'));
    },
  });

  const handleSave = () => {
    const toolheadDtos: CalibrationToolheadSetupDto[] = toolheads.map((t) => ({
      id: t.id,
      offsetX: toNullableNumber(t.offsetX),
      offsetY: toNullableNumber(t.offsetY),
      offsetZ: toNullableNumber(t.offsetZ),
      driveType: t.driveType.trim() === '' ? null : t.driveType,
      isDirectDrive: t.isDirectDrive,
      extruderGearRatio: t.extruderGearRatio.trim() === '' ? null : t.extruderGearRatio,
      maxVolumetricFlow: toNullableNumber(t.maxVolumetricFlow),
      nozzleMaterial: t.nozzleMaterial.trim() === '' ? null : t.nozzleMaterial,
      nozzleIsHardened: t.nozzleIsHardened,
    }));

    const excludedRegions: CalibrationExcludedRegionDto[] = regions.map((r) => ({
      name: r.name.trim() === '' ? null : r.name,
      polygon: r.points.map((p) => ({ x: Number(p.x) || 0, y: Number(p.y) || 0 })),
    }));

    const request: CalibrationSetupRequestDto = {
      activeToolheadIndex,
      // Always submit explicitly, including an empty array, per AC #2.
      excludedRegions,
      supportsPressureAdvance,
      supportsFirmwareRetraction,
      toolheads: toolheadDtos,
    };
    setupMutation.mutate(request);
  };

  const handleMarkHardwareVerified = () => {
    setupMutation.mutate({ calibrationHardwareVerifiedAtUtc: new Date().toISOString() });
  };

  const handleMarkFirmwareVerified = () => {
    setupMutation.mutate({ firmwareIdentityVerified: true });
  };

  const updateToolhead = (index: number, patch: Partial<ToolheadFormState>) => {
    setToolheads((prev) => prev.map((t, i) => (i === index ? { ...t, ...patch } : t)));
  };

  const addRegion = () => {
    setRegions((prev) => [...prev, { name: '', points: [{ x: '0', y: '0' }] }]);
  };

  const removeRegion = (index: number) => {
    setRegions((prev) => prev.filter((_, i) => i !== index));
  };

  const updateRegionName = (index: number, name: string) => {
    setRegions((prev) => prev.map((r, i) => (i === index ? { ...r, name } : r)));
  };

  const addPoint = (regionIndex: number) => {
    setRegions((prev) =>
      prev.map((r, i) => (i === regionIndex ? { ...r, points: [...r.points, { x: '0', y: '0' }] } : r))
    );
  };

  const removePoint = (regionIndex: number, pointIndex: number) => {
    setRegions((prev) =>
      prev.map((r, i) => (i === regionIndex ? { ...r, points: r.points.filter((_, pi) => pi !== pointIndex) } : r))
    );
  };

  const updatePoint = (regionIndex: number, pointIndex: number, axis: 'x' | 'y', value: string) => {
    setRegions((prev) =>
      prev.map((r, i) =>
        i === regionIndex
          ? { ...r, points: r.points.map((p, pi) => (pi === pointIndex ? { ...p, [axis]: value } : p)) }
          : r
      )
    );
  };

  const firmware = contextQuery.data?.firmware;

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={`Calibration setup — ${printerName}`}
      size="xl"
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Close
          </Button>
          <Button variant="primary" onClick={handleSave} disabled={setupMutation.isPending || contextQuery.isLoading}>
            {setupMutation.isPending ? 'Saving…' : 'Save calibration setup'}
          </Button>
        </>
      }
    >
      {contextQuery.isLoading && <p className="text-sm text-pf-text-secondary">Loading calibration context…</p>}
      {contextQuery.isError && (
        <Alert type="error">Failed to load calibration context. Try reopening this dialog.</Alert>
      )}
      {contextQuery.data && (
        <div className="flex flex-col gap-4">
          <Alert type={contextQuery.data.eligible ? 'success' : 'warning'}>
            {contextQuery.data.eligible
              ? 'This printer is currently eligible for calibration.'
              : `Not yet eligible. Missing: ${contextQuery.data.missingInputs.join(', ') || 'unknown'}`}
          </Alert>

          <CollapsibleSection title="Firmware identity (read-only)">
            <div className="flex flex-col gap-2 text-sm">
              <p>
                Family: <strong>{firmware?.family ?? 'Unknown'}</strong> · Version:{' '}
                <strong>{firmware?.version ?? 'Unknown'}</strong> · G-code dialect:{' '}
                <strong>{firmware?.gcodeDialect ?? 'Unknown'}</strong>
              </p>
              <p className="text-pf-text-secondary">
                These facts are detected automatically and cannot be edited here. Confirm them once verified against
                the physical hardware.
              </p>
              <div>
                <Button
                  variant={firmware?.verified ? 'secondary' : 'primary'}
                  size="sm"
                  onClick={handleMarkFirmwareVerified}
                  disabled={setupMutation.isPending}
                >
                  {firmware?.verified ? 'Firmware verified ✓' : 'Mark firmware verified'}
                </Button>
              </div>
            </div>
          </CollapsibleSection>

          <CollapsibleSection title="Hardware sign-off">
            <div className="flex flex-col gap-2 text-sm">
              <p>
                Verified at:{' '}
                <strong>{hardwareVerifiedAtUtc ? new Date(hardwareVerifiedAtUtc).toLocaleString() : 'Not verified'}</strong>
              </p>
              <div>
                <Button variant="primary" size="sm" onClick={handleMarkHardwareVerified} disabled={setupMutation.isPending}>
                  Mark hardware verified now
                </Button>
              </div>
            </div>
          </CollapsibleSection>

          <CollapsibleSection title="Capability flags">
            <div className="flex flex-col gap-2">
              <Checkbox
                label="Supports pressure advance"
                checked={supportsPressureAdvance}
                onChange={(e) => setSupportsPressureAdvance(e.target.checked)}
              />
              <Checkbox
                label="Supports firmware retraction"
                checked={supportsFirmwareRetraction}
                onChange={(e) => setSupportsFirmwareRetraction(e.target.checked)}
              />
            </div>
          </CollapsibleSection>

          <CollapsibleSection title="Active toolhead">
            <div className="flex flex-col gap-2">
              <p className="text-xs text-pf-text-secondary">
                Only the active toolhead&apos;s nozzle geometry is derived from the resolved machine profile. Every
                other toolhead — and every metrology field below on every toolhead — is manual.
              </p>
              <Select
                aria-label="Active toolhead"
                value={activeToolheadIndex ?? ''}
                onChange={(e) => setActiveToolheadIndex(e.target.value === '' ? null : Number(e.target.value))}
              >
                <option value="">Unset</option>
                {toolheads.map((t) => (
                  <option key={t.id} value={t.index}>
                    {t.name} (index {t.index})
                  </option>
                ))}
              </Select>
            </div>
          </CollapsibleSection>

          <CollapsibleSection title="Excluded regions">
            <div className="flex flex-col gap-3">
              {regions.length === 0 && (
                <p className="text-xs text-pf-text-secondary">
                  No excluded regions. Saving now will explicitly submit an empty list.
                </p>
              )}
              {regions.map((region, regionIndex) => (
                <div key={regionIndex} className="border border-pf-border rounded-sm p-2 flex flex-col gap-2">
                  <div className="flex items-center gap-2">
                    <Input
                      aria-label={`Region ${regionIndex + 1} name`}
                      placeholder="Region name"
                      value={region.name}
                      onChange={(e) => updateRegionName(regionIndex, e.target.value)}
                    />
                    <Button variant="danger" size="sm" onClick={() => removeRegion(regionIndex)}>
                      Remove region
                    </Button>
                  </div>
                  {region.points.map((point, pointIndex) => (
                    <div key={pointIndex} className="flex items-center gap-2">
                      <Input
                        aria-label={`Region ${regionIndex + 1} point ${pointIndex + 1} X`}
                        type="number"
                        value={point.x}
                        onChange={(e) => updatePoint(regionIndex, pointIndex, 'x', e.target.value)}
                      />
                      <Input
                        aria-label={`Region ${regionIndex + 1} point ${pointIndex + 1} Y`}
                        type="number"
                        value={point.y}
                        onChange={(e) => updatePoint(regionIndex, pointIndex, 'y', e.target.value)}
                      />
                      <Button variant="subtle" size="sm" onClick={() => removePoint(regionIndex, pointIndex)}>
                        Remove point
                      </Button>
                    </div>
                  ))}
                  <div>
                    <Button variant="subtle" size="sm" onClick={() => addPoint(regionIndex)}>
                      Add point
                    </Button>
                  </div>
                </div>
              ))}
              <div>
                <Button variant="secondary" size="sm" onClick={addRegion}>
                  Add excluded region
                </Button>
              </div>
            </div>
          </CollapsibleSection>

          {toolheads.map((toolhead, index) => (
            <CollapsibleSection key={toolhead.id} title={`Toolhead metrology — ${toolhead.name}`} defaultExpanded={index === 0}>
              <div className="grid grid-cols-2 gap-2">
                <label className="flex flex-col gap-1 text-xs">
                  Offset X (mm)
                  <Input
                    type="number"
                    value={toolhead.offsetX}
                    onChange={(e) => updateToolhead(index, { offsetX: e.target.value })}
                  />
                </label>
                <label className="flex flex-col gap-1 text-xs">
                  Offset Y (mm)
                  <Input
                    type="number"
                    value={toolhead.offsetY}
                    onChange={(e) => updateToolhead(index, { offsetY: e.target.value })}
                  />
                </label>
                <label className="flex flex-col gap-1 text-xs">
                  Offset Z (mm)
                  <Input
                    type="number"
                    value={toolhead.offsetZ}
                    onChange={(e) => updateToolhead(index, { offsetZ: e.target.value })}
                  />
                </label>
                <label className="flex flex-col gap-1 text-xs">
                  Drive type
                  <Select
                    aria-label={`${toolhead.name} drive type`}
                    value={toolhead.driveType}
                    onChange={(e) => updateToolhead(index, { driveType: e.target.value })}
                  >
                    <option value="">Unset</option>
                    <option value="direct">Direct</option>
                    <option value="bowden">Bowden</option>
                  </Select>
                </label>
                <label className="flex items-center gap-2 text-xs">
                  <Checkbox
                    checked={toolhead.isDirectDrive}
                    onChange={(e) => updateToolhead(index, { isDirectDrive: e.target.checked })}
                  />
                  Is direct drive
                </label>
                <label className="flex flex-col gap-1 text-xs">
                  Extruder gear ratio
                  <Input
                    value={toolhead.extruderGearRatio}
                    placeholder="e.g. 3:1"
                    onChange={(e) => updateToolhead(index, { extruderGearRatio: e.target.value })}
                  />
                </label>
                <label className="flex flex-col gap-1 text-xs">
                  Max volumetric flow (mm³/s)
                  <Input
                    type="number"
                    value={toolhead.maxVolumetricFlow}
                    onChange={(e) => updateToolhead(index, { maxVolumetricFlow: e.target.value })}
                  />
                </label>
                <label className="flex flex-col gap-1 text-xs">
                  Nozzle material
                  <Input
                    value={toolhead.nozzleMaterial}
                    onChange={(e) => updateToolhead(index, { nozzleMaterial: e.target.value })}
                  />
                </label>
                <label className="flex items-center gap-2 text-xs">
                  <Checkbox
                    checked={toolhead.nozzleIsHardened}
                    onChange={(e) => updateToolhead(index, { nozzleIsHardened: e.target.checked })}
                  />
                  Nozzle is hardened
                </label>
              </div>
            </CollapsibleSection>
          ))}
        </div>
      )}
    </Modal>
  );
}

export default CalibrationSetupModal;
