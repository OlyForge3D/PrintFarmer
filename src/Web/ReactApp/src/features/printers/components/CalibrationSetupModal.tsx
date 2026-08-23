import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Modal } from '@/common/components/modals/Modal';
import { Button, Alert, Input, Select, CollapsibleSection } from '@/common/components/ui';
import { apiClient } from '@/services/api';
import { slicerProfilesService } from '@/services/slicerProfilesService';
import { mutationErrorMessage } from '@/common/utils/mutationError';
import type {
  CalibrationContextDto,
  CalibrationExcludedRegionDto,
  CalibrationSetupRequestDto,
  CalibrationToolheadSetupDto,
} from '@/types/api';
import {
  CALIBRATION_FIELD_GROUPS,
  getCalibrationStatus,
  groupMissingInputs,
  type CalibrationFieldGroup,
} from '../utils/calibrationFieldGuidance';

/** Display order for resolution-group cards in the "remaining work" section. */
const CALIBRATION_FIELD_GROUP_ORDER: CalibrationFieldGroup[] = [
  'here',
  'profile',
  'signoff',
  'admin',
  'other',
];

/** The all-zero Guid the calibration-setup endpoint interprets as "clear this binding".
 * Omitting the field means "leave unchanged", so an explicit sentinel is required to
 * distinguish an operator unbinding a profile from an operator not touching it. */
const CLEAR_PROFILE_ID = '00000000-0000-0000-0000-000000000000';

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
  isDirectDrive: boolean | null;
  extruderGearRatio: string;
  maxVolumetricFlow: string;
  nozzleMaterial: string;
  nozzleIsHardened: boolean | null;
}

/** Renders a tri-state (unknown/yes/no) selector for a nullable boolean fact,
 * so leaving a field untouched never coerces "unknown" into an explicit
 * `false` on save (these flags are `bool?` on the backend, where `null` means
 * "not yet answered" and is distinct from a confirmed `false`). */
function triStateToOption(value: boolean | null): string {
  if (value === true) return 'true';
  if (value === false) return 'false';
  return '';
}

function optionToTriState(value: string): boolean | null {
  if (value === 'true') return true;
  if (value === 'false') return false;
  return null;
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
    isDirectDrive: t.isDirectDrive ?? null,
    extruderGearRatio: t.extruderGearRatio ?? '',
    maxVolumetricFlow: t.maxVolumetricFlow != null ? String(t.maxVolumetricFlow) : '',
    nozzleMaterial: t.nozzleMaterial ?? '',
    nozzleIsHardened: t.nozzleIsHardened ?? null,
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
  const [supportsPressureAdvance, setSupportsPressureAdvance] = useState<boolean | null>(null);
  const [supportsFirmwareRetraction, setSupportsFirmwareRetraction] = useState<boolean | null>(null);
  const [hardwareVerifiedAtUtc, setHardwareVerifiedAtUtc] = useState<string | null>(null);
  const [regions, setRegions] = useState<RegionFormState[]>([]);
  const [toolheads, setToolheads] = useState<ToolheadFormState[]>([]);
  const [machineProfileId, setMachineProfileId] = useState<string>('');
  const [processProfileId, setProcessProfileId] = useState<string>('');
  const [filamentProfileId, setFilamentProfileId] = useState<string>('');
  const [latestRowVersion, setLatestRowVersion] = useState<string | null>(rowVersion ?? null);

  const profilesQuery = useQuery({
    queryKey: ['slicer-profiles', 'extended'],
    queryFn: () => slicerProfilesService.listExtended(),
    enabled: isOpen,
  });

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
    setSupportsPressureAdvance(data.supportsPressureAdvance ?? null);
    setSupportsFirmwareRetraction(data.supportsFirmwareRetraction ?? null);
    setHardwareVerifiedAtUtc(data.calibrationHardwareVerifiedAtUtc ?? null);
    setRegions(regionsFromContext(data.excludedRegions ?? []));
    setToolheads((data.toolheads ?? []).map(toolheadFromContext));
    setMachineProfileId(data.slicer?.machineProfileId ?? '');
    setProcessProfileId(data.slicer?.processProfileId ?? '');
    setFilamentProfileId(data.slicer?.filamentProfileId ?? '');
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

  const detectFirmwareMutation = useMutation({
    mutationFn: () => apiClient.detectPrinterFirmware(printerId),
    onSuccess: (result) => {
      // The detected facts land on the printer row, so the calibration context has to be
      // refetched for the gate to stop reporting the firmware inputs as missing.
      queryClient.invalidateQueries({ queryKey: calibrationContextQueryKey });
      queryClient.invalidateQueries({ queryKey: ['printers'] });
      toast.success(
        result.version
          ? `Firmware detected: ${result.family ?? 'Unknown'} ${result.version}`
          : `Firmware detected: ${result.family ?? 'Unknown'}`
      );
    },
    onError: (error: unknown) => {
      toast.error(mutationErrorMessage(error, 'Failed to detect firmware'));
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
      // An empty selection is submitted as the clear sentinel rather than omitted,
      // so deliberately unbinding a profile is saved instead of silently ignored.
      machineProfileId: machineProfileId === '' ? CLEAR_PROFILE_ID : machineProfileId,
      processProfileId: processProfileId === '' ? CLEAR_PROFILE_ID : processProfileId,
      filamentProfileId: filamentProfileId === '' ? CLEAR_PROFILE_ID : filamentProfileId,
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

  const data = contextQuery.data;
  const allProfilesBound = Boolean(
    data?.slicer?.machineProfileId && data?.slicer?.processProfileId && data?.slicer?.filamentProfileId
  );
  const groupedMissingInputs = data
    ? groupMissingInputs(data.missingInputs, allProfilesBound, data.activeToolheadIndex ?? null)
    : null;
  const calibrationStatus = data
    ? getCalibrationStatus({
        eligible: data.eligible,
        anyProfileBound: allProfilesBound,
        // `firmware.family` always reports a non-null string ("Unknown" for a never-probed
        // printer, per CalibrationFirmwareIdentityDto), so a bare truthiness check would
        // always read as "detected". Mirror the backend's own HasRecordedIdentity gate:
        // a known family plus a recorded version string.
        firmwareDetected: Boolean(data.firmware?.family && data.firmware.family !== 'Unknown' && data.firmware?.version),
        hardwareOrFirmwareVerified: Boolean(data.calibrationHardwareVerifiedAtUtc || data.firmware?.verified),
      })
    : null;
  const statusHeadline =
    calibrationStatus === 'ready'
      ? 'Ready — this printer is eligible for calibration.'
      : calibrationStatus === 'in-progress'
        ? 'Setup in progress — a few things are still needed before calibration is possible.'
        : "Calibration setup needed — this printer hasn't been set up for calibration yet.";

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
          <Alert type={calibrationStatus === 'ready' ? 'success' : 'info'}>{statusHeadline}</Alert>

          {calibrationStatus !== 'ready' && groupedMissingInputs && (
            <div className="flex flex-col gap-2">
              {CALIBRATION_FIELD_GROUP_ORDER.map((groupKey) => {
                const items = groupedMissingInputs[groupKey];
                if (items.length === 0) return null;
                const groupInfo = CALIBRATION_FIELD_GROUPS[groupKey];
                return (
                  <div key={groupKey} className="border border-pf-border rounded-sm p-2">
                    <p className="text-sm font-semibold">{groupInfo.title}</p>
                    <p className="text-xs text-pf-text-secondary mb-1">{groupInfo.description}</p>
                    <ul className="list-disc list-inside text-sm">
                      {items.map((item) => (
                        <li key={item.path}>{item.label}</li>
                      ))}
                    </ul>
                  </div>
                );
              })}
              {contextQuery.data.missingInputs.length > 0 && (
                <CollapsibleSection title="Technical details (raw field paths)" defaultExpanded={false}>
                  <ul className="list-disc list-inside text-xs font-mono text-pf-text-secondary">
                    {contextQuery.data.missingInputs.map((path) => (
                      <li key={path}>{path}</li>
                    ))}
                  </ul>
                </CollapsibleSection>
              )}
            </div>
          )}

          <CollapsibleSection title="Slicer profiles" defaultExpanded>
            <div className="flex flex-col gap-2">
              <p className="text-xs text-pf-text-secondary">
                Calibration sources bed origin, printable area, motion limits, and nozzle facts from the bound
                machine profile. All three profiles must be bound before this printer can be calibrated.
              </p>
              {profilesQuery.isError && (
                <Alert type="error">Failed to load slicer profiles. Try reopening this dialog.</Alert>
              )}
              <label className="flex flex-col gap-1 text-xs">
                Machine profile
                <Select
                  aria-label="Machine profile"
                  value={machineProfileId}
                  disabled={profilesQuery.isLoading}
                  onChange={(e) => setMachineProfileId(e.target.value)}
                >
                  <option value="">Not bound</option>
                  {(profilesQuery.data?.machineProfiles ?? []).map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.manufacturer ? `${p.manufacturer} — ${p.name}` : p.name}
                    </option>
                  ))}
                </Select>
              </label>
              <label className="flex flex-col gap-1 text-xs">
                Process profile
                <Select
                  aria-label="Process profile"
                  value={processProfileId}
                  disabled={profilesQuery.isLoading}
                  onChange={(e) => setProcessProfileId(e.target.value)}
                >
                  <option value="">Not bound</option>
                  {(profilesQuery.data?.processProfiles ?? []).map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.name}
                    </option>
                  ))}
                </Select>
              </label>
              <label className="flex flex-col gap-1 text-xs">
                Filament profile
                <Select
                  aria-label="Filament profile"
                  value={filamentProfileId}
                  disabled={profilesQuery.isLoading}
                  onChange={(e) => setFilamentProfileId(e.target.value)}
                >
                  <option value="">Not bound</option>
                  {(profilesQuery.data?.filamentProfiles ?? []).map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.name}
                    </option>
                  ))}
                </Select>
              </label>
            </div>
          </CollapsibleSection>

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
              <p className="text-pf-text-secondary">
                The firmware version shown elsewhere in the app is read live from the printer and is not the same
                record calibration reads. If calibration still reports firmware as missing, re-probe here.
              </p>
              <div className="flex flex-wrap gap-2">
                <Button
                  variant="secondary"
                  size="sm"
                  onClick={() => detectFirmwareMutation.mutate()}
                  disabled={detectFirmwareMutation.isPending || setupMutation.isPending}
                >
                  {detectFirmwareMutation.isPending ? 'Detecting…' : 'Re-probe firmware'}
                </Button>
                <Button
                  variant={firmware?.verified ? 'secondary' : 'primary'}
                  size="sm"
                  onClick={handleMarkFirmwareVerified}
                  disabled={setupMutation.isPending || detectFirmwareMutation.isPending}
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
              <label className="flex flex-col gap-1 text-xs">
                Supports pressure advance
                <Select
                  aria-label="Supports pressure advance"
                  value={triStateToOption(supportsPressureAdvance)}
                  onChange={(e) => setSupportsPressureAdvance(optionToTriState(e.target.value))}
                >
                  <option value="">Unknown</option>
                  <option value="true">Yes</option>
                  <option value="false">No</option>
                </Select>
              </label>
              <label className="flex flex-col gap-1 text-xs">
                Supports firmware retraction
                <Select
                  aria-label="Supports firmware retraction"
                  value={triStateToOption(supportsFirmwareRetraction)}
                  onChange={(e) => setSupportsFirmwareRetraction(optionToTriState(e.target.value))}
                >
                  <option value="">Unknown</option>
                  <option value="true">Yes</option>
                  <option value="false">No</option>
                </Select>
              </label>
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
                <label className="flex flex-col gap-1 text-xs">
                  Is direct drive
                  <Select
                    aria-label={`${toolhead.name} is direct drive`}
                    value={triStateToOption(toolhead.isDirectDrive)}
                    onChange={(e) => updateToolhead(index, { isDirectDrive: optionToTriState(e.target.value) })}
                  >
                    <option value="">Unknown</option>
                    <option value="true">Yes</option>
                    <option value="false">No</option>
                  </Select>
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
                <label className="flex flex-col gap-1 text-xs">
                  Nozzle is hardened
                  <Select
                    aria-label={`${toolhead.name} nozzle is hardened`}
                    value={triStateToOption(toolhead.nozzleIsHardened)}
                    onChange={(e) => updateToolhead(index, { nozzleIsHardened: optionToTriState(e.target.value) })}
                  >
                    <option value="">Unknown</option>
                    <option value="true">Yes</option>
                    <option value="false">No</option>
                  </Select>
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
