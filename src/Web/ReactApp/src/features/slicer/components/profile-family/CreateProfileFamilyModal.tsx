import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Modal } from '@/common/components/modals/Modal';
import { Alert, Button, Card, Checkbox, FormField, Input, Spinner } from '@/common/components/ui';
import { SearchIcon } from '@/common/components/icons/MdiIcons';
import { isApiError } from '@/common/utils/apiErrors';
import { renderUnknown } from '@/common/utils/renderUnknown';
import { generateUUID } from '@/utils/uuid';
import {
  slicerProfilesService,
  type CloneProfileFamilyRequest,
  type CloneProfileFamilyResponse,
  type CustomProfile,
  type OrcaMachineProfile,
  type WorkerPrinterModelProfilesDto,
} from '@/services/slicerProfilesService';

const MAX_FAMILY_NAME_LENGTH = 256;
const ORCA_DISTRIBUTION = 'OrcaSlicer';
const WIZARD_STEPS = ['Choose source', 'Name family', 'Select nozzles', 'Shared overrides', 'Review', 'Confirm'] as const;
const IDENTITY_OVERRIDE_KEYS = new Set([
  'name',
  'from',
  'inherits',
  'printer_model',
  'printer_notes',
  'nozzle_diameter',
  'nozzle_type',
  'printer_variant',
  'min_layer_height',
  'max_layer_height',
  'default_print_profile',
  'setting_id',
  'type',
  'instantiation',
]);

interface SelectedSourceModel {
  manufacturer: string;
  modelName: string;
  model: WorkerPrinterModelProfilesDto;
  availableNozzles: number[];
}

interface AdvancedOverrideRow {
  id: string;
  key: string;
  value: string;
}

interface ProfileFamilyApiErrorBody {
  code?: string;
  detail?: string;
}

interface CreateProfileFamilyModalProps {
  isOpen: boolean;
  onClose: () => void;
  targetPrinterModelId: string;
  targetPrinterModelName: string;
  defaultNozzleDiameter?: number;
  slicerEngineVersion?: string;
  onSuccess: (response: CloneProfileFamilyResponse) => void;
}

function formatNumber(value: number) {
  return Number(value.toFixed(3)).toString();
}

function uniqueSortedNozzles(machineProfiles: OrcaMachineProfile[] = []) {
  return Array.from(new Set(
    machineProfiles
      .map((profile) => profile.nozzleDiameter)
      .filter((diameter): diameter is number => typeof diameter === 'number' && Number.isFinite(diameter) && diameter > 0)
      .map((diameter) => Number(diameter.toFixed(3)))
  )).sort((a, b) => a - b);
}

function getSettingValue(settings: Record<string, unknown>, key: string): unknown {
  const value = settings[key];
  return Array.isArray(value) ? value[0] : value;
}

function parsePrintableArea(value: unknown) {
  if (typeof value !== 'string') return { x: '', y: '' };
  const parsed = value
    .split(',')
    .map((point) => point.trim().split('x').map((segment) => Number.parseFloat(segment)))
    .filter(([x, y]) => Number.isFinite(x) && Number.isFinite(y));
  if (parsed.length === 0) return { x: '', y: '' };
  const maxX = Math.max(...parsed.map(([x]) => x));
  const maxY = Math.max(...parsed.map(([, y]) => y));
  return { x: maxX > 0 ? formatNumber(maxX) : '', y: maxY > 0 ? formatNumber(maxY) : '' };
}

function parseNumberSetting(value: unknown) {
  if (typeof value === 'number' && Number.isFinite(value)) return formatNumber(value);
  if (typeof value === 'string') {
    const parsed = Number.parseFloat(value);
    return Number.isFinite(parsed) ? formatNumber(parsed) : '';
  }
  return '';
}

function buildPrintableArea(xValue: string, yValue: string) {
  const x = Number.parseFloat(xValue);
  const y = Number.parseFloat(yValue);
  if (!Number.isFinite(x) || !Number.isFinite(y) || x <= 0 || y <= 0) return undefined;
  return `0x0,${formatNumber(x)}x0,${formatNumber(x)}x${formatNumber(y)},0x${formatNumber(y)}`;
}

function parseAdvancedValue(value: string): unknown {
  const trimmed = value.trim();
  if (trimmed === '') return '';
  if (trimmed === 'true') return true;
  if (trimmed === 'false') return false;
  if (trimmed === 'null') return null;
  const numeric = Number(trimmed);
  if (Number.isFinite(numeric)) return numeric;
  if ((trimmed.startsWith('{') && trimmed.endsWith('}')) || (trimmed.startsWith('[') && trimmed.endsWith(']'))) {
    try {
      return JSON.parse(trimmed) as unknown;
    } catch {
      return trimmed;
    }
  }
  return trimmed;
}

function getApiErrorBody(error: unknown) {
  if (isApiError(error)) {
    const data = error.data as ProfileFamilyApiErrorBody | undefined;
    return { statusCode: error.statusCode, code: data?.code, detail: data?.detail, message: error.message };
  }
  if (error instanceof Error) return { message: error.message };
  return {};
}

export function CreateProfileFamilyModal({
  isOpen,
  onClose,
  targetPrinterModelId,
  targetPrinterModelName,
  defaultNozzleDiameter,
  slicerEngineVersion,
  onSuccess,
}: CreateProfileFamilyModalProps) {
  const queryClient = useQueryClient();
  const [stepIndex, setStepIndex] = useState(0);
  const [searchQuery, setSearchQuery] = useState('');
  const [selectedSource, setSelectedSource] = useState<SelectedSourceModel | null>(null);
  const [familyName, setFamilyName] = useState('');
  const [nameError, setNameError] = useState('');
  const [selectedNozzles, setSelectedNozzles] = useState<Set<number>>(new Set());
  const [bedX, setBedX] = useState('');
  const [bedY, setBedY] = useState('');
  const [printableHeight, setPrintableHeight] = useState('');
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const [advancedOverrides, setAdvancedOverrides] = useState<AdvancedOverrideRow[]>([]);
  const [reviewAlert, setReviewAlert] = useState('');
  const [isSubmitting, setIsSubmitting] = useState(false);
  const dirtyRef = useRef(false);
  const nameInputRef = useRef<HTMLInputElement>(null);

  const { data: hierarchy, isLoading: hierarchyLoading, error: hierarchyError } = useQuery({
    queryKey: ['slicerProfilesWorkerHierarchy'],
    queryFn: () => slicerProfilesService.getWorkerHierarchy(),
    enabled: isOpen,
    staleTime: 60_000,
  });

  const { data: customProfiles } = useQuery({
    queryKey: ['customProfiles'],
    queryFn: () => slicerProfilesService.listCustomProfiles(),
    enabled: isOpen,
    staleTime: 30_000,
  });

  useEffect(() => {
    if (!isOpen) return;
    setStepIndex(0);
    setSearchQuery('');
    setSelectedSource(null);
    setFamilyName('');
    setNameError('');
    setSelectedNozzles(new Set());
    setBedX('');
    setBedY('');
    setPrintableHeight('');
    setAdvancedOpen(false);
    setAdvancedOverrides([]);
    setReviewAlert('');
    setIsSubmitting(false);
    dirtyRef.current = false;
  }, [isOpen]);

  useEffect(() => {
    if (!selectedSource) return;
    const sourceProfile = selectedSource.model.machineProfiles?.[0];
    const settings = sourceProfile?.settings ?? {};
    const area = parsePrintableArea(getSettingValue(settings, 'printable_area'));
    setBedX(area.x);
    setBedY(area.y);
    setPrintableHeight(parseNumberSetting(getSettingValue(settings, 'printable_height')));
    setSelectedNozzles(new Set(selectedSource.availableNozzles));
  }, [selectedSource]);

  const sourceGroups = useMemo(() => {
    const groups = Object.entries(hierarchy?.byHierarchy ?? {})
      .map(([manufacturerKey, manufacturer]) => {
        const models = Object.entries(manufacturer.models ?? {})
          .map(([modelKey, model]) => ({
            manufacturer: manufacturer.name || manufacturerKey,
            modelName: model.name || modelKey,
            model,
            availableNozzles: uniqueSortedNozzles(model.machineProfiles),
          }))
          .filter((model) => model.availableNozzles.length > 0)
          .sort((a, b) => a.modelName.localeCompare(b.modelName));
        return { manufacturer: manufacturer.name || manufacturerKey, models };
      })
      .filter((group) => group.models.length > 0)
      .sort((a, b) => a.manufacturer.localeCompare(b.manufacturer));

    const query = searchQuery.trim().toLowerCase();
    if (!query) return groups;
    return groups
      .map((group) => ({
        ...group,
        models: group.models.filter(
          (model) => model.manufacturer.toLowerCase().includes(query) || model.modelName.toLowerCase().includes(query)
        ),
      }))
      .filter((group) => group.models.length > 0);
  }, [hierarchy, searchQuery]);

  const customProfileNames = useMemo(() => {
    const profiles = customProfiles?.profiles ?? [];
    return new Set(profiles.map((profile: CustomProfile) => profile.name.trim().toLowerCase()).filter(Boolean));
  }, [customProfiles]);

  const nameCollisionWarning = useMemo(() => {
    const normalized = familyName.trim().toLowerCase();
    if (!normalized) return '';
    return customProfileNames.has(normalized)
      ? 'A custom profile with this family name already exists. The server will make the final decision.'
      : '';
  }, [customProfileNames, familyName]);

  const sourceMachineByNozzle = useMemo(() => {
    const byNozzle = new Map<number, OrcaMachineProfile>();
    for (const profile of selectedSource?.model.machineProfiles ?? []) {
      if (typeof profile.nozzleDiameter !== 'number') continue;
      const normalized = Number(profile.nozzleDiameter.toFixed(3));
      if (!byNozzle.has(normalized)) byNozzle.set(normalized, profile);
    }
    return byNozzle;
  }, [selectedSource]);

  const selectedNozzleList = useMemo(() => Array.from(selectedNozzles).sort((a, b) => a - b), [selectedNozzles]);

  const advancedOverrideErrors = useMemo(() => {
    const errors = new Map<string, string>();
    for (const row of advancedOverrides) {
      const key = row.key.trim();
      if (key && IDENTITY_OVERRIDE_KEYS.has(key)) errors.set(row.id, `“${key}” is an identity key and cannot be sent.`);
    }
    return errors;
  }, [advancedOverrides]);

  const familyOverrides = useMemo(() => {
    const overrides: Record<string, unknown> = {};
    const area = buildPrintableArea(bedX, bedY);
    if (area) overrides.printable_area = area;
    const height = Number.parseFloat(printableHeight);
    if (Number.isFinite(height) && height > 0) overrides.printable_height = height;
    for (const row of advancedOverrides) {
      const key = row.key.trim();
      if (!key || IDENTITY_OVERRIDE_KEYS.has(key)) continue;
      overrides[key] = parseAdvancedValue(row.value);
    }
    return overrides;
  }, [advancedOverrides, bedX, bedY, printableHeight]);

  const markDirty = () => {
    dirtyRef.current = true;
  };

  const requestClose = useCallback(() => {
    if (!dirtyRef.current || window.confirm('Discard this profile family draft?')) onClose();
  }, [onClose]);

  const validateStepTwo = useCallback(() => {
    const trimmed = familyName.trim();
    if (!trimmed) {
      setNameError('Enter a family name.');
      nameInputRef.current?.focus();
      return false;
    }
    if (trimmed.length > MAX_FAMILY_NAME_LENGTH) {
      setNameError(`Family name must be ${MAX_FAMILY_NAME_LENGTH} characters or fewer.`);
      nameInputRef.current?.focus();
      return false;
    }
    setNameError('');
    return true;
  }, [familyName]);

  const canAdvanceCurrentStep = useMemo(() => {
    if (stepIndex === 0) return selectedSource !== null;
    if (stepIndex === 1) return familyName.trim().length > 0 && familyName.trim().length <= MAX_FAMILY_NAME_LENGTH;
    if (stepIndex === 2) return selectedNozzles.size > 0;
    if (stepIndex === 3) return advancedOverrideErrors.size === 0;
    return true;
  }, [advancedOverrideErrors.size, familyName, selectedNozzles.size, selectedSource, stepIndex]);
  const currentStepName = WIZARD_STEPS[stepIndex];

  const validateCurrentStep = useCallback(() => {
    if (stepIndex === 1) return validateStepTwo();
    return canAdvanceCurrentStep;
  }, [canAdvanceCurrentStep, stepIndex, validateStepTwo]);

  const goNext = useCallback(() => {
    setReviewAlert('');
    if (!validateCurrentStep()) return;
    setStepIndex((current) => Math.min(current + 1, WIZARD_STEPS.length - 1));
  }, [validateCurrentStep]);

  const goBack = useCallback(() => {
    setReviewAlert('');
    setStepIndex((current) => Math.max(current - 1, 0));
  }, []);

  const handleKeyDown = useCallback((event: React.KeyboardEvent<HTMLDivElement>) => {
    if (event.key === 'Enter' && stepIndex < WIZARD_STEPS.length - 1 && !event.shiftKey) {
      const target = event.target as HTMLElement;
      if (target.tagName !== 'BUTTON') {
        event.preventDefault();
        goNext();
      }
    }
    if (event.key === 'Escape') {
      event.preventDefault();
      requestClose();
    }
  }, [goNext, requestClose, stepIndex]);

  const addAdvancedOverride = () => {
    markDirty();
    setAdvancedOverrides((rows) => [...rows, { id: generateUUID(), key: '', value: '' }]);
  };

  const updateAdvancedOverride = (id: string, patch: Partial<AdvancedOverrideRow>) => {
    markDirty();
    setAdvancedOverrides((rows) => rows.map((row) => (row.id === id ? { ...row, ...patch } : row)));
  };

  const removeAdvancedOverride = (id: string) => {
    markDirty();
    setAdvancedOverrides((rows) => rows.filter((row) => row.id !== id));
  };

  const buildRequest = (): CloneProfileFamilyRequest | null => {
    if (!selectedSource) return null;
    return {
      familyName: familyName.trim(),
      targetPrinterModelId,
      sourceManufacturer: selectedSource.manufacturer,
      sourceMachineModelName: selectedSource.modelName,
      nozzleDiameters: selectedNozzleList,
      familyOverrides,
      slicerEngineVersion,
      slicerDistribution: ORCA_DISTRIBUTION,
    };
  };

  const handleSubmit = async () => {
    const request = buildRequest();
    if (!request || isSubmitting) return;
    setIsSubmitting(true);
    setReviewAlert('');
    try {
      const response = await slicerProfilesService.cloneFamily(request);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['customProfiles'] }),
        queryClient.invalidateQueries({ queryKey: ['machineProfilesForModel', targetPrinterModelId, slicerEngineVersion ?? null] }),
        queryClient.invalidateQueries({ queryKey: ['slicerProfilesExtended'] }),
        queryClient.invalidateQueries({ queryKey: ['slicerProfilesHierarchy'] }),
        queryClient.invalidateQueries({ queryKey: ['slicerProfilesWorkerHierarchy'] }),
      ]);
      toast.success(`Family '${response.familyName}' created with ${response.machineProfiles.length} machine variant(s), ${response.processProfileCount} process profile(s), and ${response.filamentProfileCount} filament profile(s).`);
      dirtyRef.current = false;
      onSuccess(response);
      onClose();
    } catch (error) {
      const apiError = getApiErrorBody(error);
      const detail = apiError.detail || apiError.message || 'Profile family creation failed.';
      if (apiError.statusCode === 409 || apiError.code === 'profile_family_name_conflict') {
        setStepIndex(1);
        setNameError(detail);
        window.requestAnimationFrame(() => nameInputRef.current?.focus());
      } else if (apiError.statusCode === 400 || apiError.code === 'invalid_profile_family') {
        setNameError(detail);
        setStepIndex(detail.toLowerCase().includes('name') ? 1 : detail.toLowerCase().includes('nozzle') ? 2 : 3);
      } else if (apiError.statusCode === 422 || apiError.code === 'source_preset_unavailable') {
        setStepIndex(4);
        setReviewAlert(detail);
      } else if (apiError.statusCode === 503 || apiError.code === 'profile_family_worker_unavailable') {
        setStepIndex(4);
        setReviewAlert('OrcaSlicer worker is unavailable. Try again in a moment.');
      } else {
        setStepIndex(4);
        setReviewAlert(detail || 'Profile family creation failed.');
      }
    } finally {
      setIsSubmitting(false);
    }
  };

  const footer = (
    <>
      <Button type="button" variant="secondary" onClick={stepIndex === 0 ? requestClose : goBack} disabled={isSubmitting}>
        {stepIndex === 0 ? 'Cancel' : 'Back'}
      </Button>
      {stepIndex < WIZARD_STEPS.length - 1 ? (
        <Button type="button" variant="primary" onClick={goNext} disabled={!canAdvanceCurrentStep}>
          Next
        </Button>
      ) : (
        <Button type="button" variant="primary" onClick={handleSubmit} loading={isSubmitting} disabled={isSubmitting}>
          Create family
        </Button>
      )}
    </>
  );

  return (
    <Modal
      isOpen={isOpen}
      onClose={requestClose}
      title="Create profile family"
      size="full"
      footer={footer}
      closeOnEscape={false}
      isDisabled={isSubmitting}
      closeAriaLabel="Close create profile family wizard"
      className="max-w-5xl"
    >
      <div className="space-y-5" onKeyDown={handleKeyDown}>
        <div className="sr-only" aria-live="polite">
          {String(currentStepName)} step {stepIndex + 1} of {WIZARD_STEPS.length}
          {reviewAlert ? `. ${reviewAlert}` : ''}
          {nameError ? `. ${nameError}` : ''}
        </div>
        <ol className="grid grid-cols-2 gap-2 md:grid-cols-6" aria-label="Profile family wizard progress">
          {WIZARD_STEPS.map((step, index) => (
            <li key={step} className={`rounded-md border px-3 py-2 text-xs ${index === stepIndex ? 'border-pf-accent bg-pf-accent-bg text-pf-text-primary' : 'border-pf-border text-pf-text-secondary'}`}>
              <span className="font-semibold">{index + 1}.</span> {String(step)}
            </li>
          ))}
        </ol>

        {stepIndex === 0 && (
          <section aria-labelledby="profile-family-step-source" className="space-y-4">
            <div>
              <h3 id="profile-family-step-source" className="text-lg font-semibold text-pf-text-primary">Choose source machine model</h3>
              <p className="text-sm text-pf-text-secondary">Pick the closest OrcaSlicer-shipped machine to use as the family template.</p>
            </div>
            <FormField label="Search source models" htmlFor="profile-family-source-search" helper="Search by manufacturer or machine model." helperId="profile-family-source-search-help">
              <div className="relative">
                <SearchIcon className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-pf-text-tertiary" aria-hidden="true" />
                <Input
                  id="profile-family-source-search"
                  data-autofocus
                  value={searchQuery}
                  onChange={(event) => { markDirty(); setSearchQuery(event.target.value); }}
                  className="pl-9"
                  aria-describedby="profile-family-source-search-help"
                  placeholder="Search manufacturers or models..."
                />
              </div>
            </FormField>
            {hierarchyLoading ? (
              <div className="flex justify-center py-12"><Spinner size="lg" /></div>
            ) : hierarchyError ? (
              <Alert type="error" title="Source models unavailable">Failed to load OrcaSlicer worker hierarchy.</Alert>
            ) : sourceGroups.length === 0 ? (
              <Alert type="info">No source machine models match your search.</Alert>
            ) : (
              <div className="max-h-104 overflow-y-auto rounded-lg border border-pf-border">
                {sourceGroups.map((group) => (
                  <div key={group.manufacturer}>
                    <h4 className="sticky top-0 bg-pf-bg-2 px-4 py-2 text-sm font-semibold text-pf-text-primary">{group.manufacturer}</h4>
                    {group.models.map((model) => {
                      const selected = selectedSource?.manufacturer === model.manufacturer && selectedSource.modelName === model.modelName;
                      return (
                        <Button
                          key={`${model.manufacturer}:${model.modelName}`}
                          type="button"
                          variant="subtle"
                          onClick={() => { markDirty(); setSelectedSource(model); }}
                          aria-pressed={selected}
                          className={`h-auto w-full justify-start rounded-none border-t border-pf-border/50 px-4 py-3 text-left font-normal enabled:hover:ring-1 enabled:hover:ring-inset enabled:hover:ring-pf-accent/50 ${selected ? 'bg-pf-accent/10 ring-1 ring-inset ring-pf-accent/70' : 'enabled:hover:bg-pf-bg-2'}`}
                        >
                          <span className="flex flex-col gap-1">
                            <span className="font-medium text-pf-text-primary">{model.modelName}</span>
                            <span className="text-xs text-pf-text-secondary">{model.availableNozzles.length} nozzle variants: {model.availableNozzles.map(formatNumber).join(', ')}</span>
                          </span>
                        </Button>
                      );
                    })}
                  </div>
                ))}
              </div>
            )}
          </section>
        )}

        {stepIndex === 1 && (
          <section aria-labelledby="profile-family-step-name" className="space-y-4">
            <div>
              <h3 id="profile-family-step-name" className="text-lg font-semibold text-pf-text-primary">Name family and confirm target</h3>
              <p className="text-sm text-pf-text-secondary">This family will be bound to printer model <span className="font-medium text-pf-text-primary">{targetPrinterModelName}</span>.</p>
            </div>
            <FormField
              label="Family name"
              htmlFor="profile-family-name"
              required
              helper={nameCollisionWarning || `Use ${MAX_FAMILY_NAME_LENGTH} characters or fewer.`}
              error={nameError}
              helperId="profile-family-name-help"
              errorId="profile-family-name-error"
            >
              <Input
                ref={nameInputRef}
                id="profile-family-name"
                value={familyName}
                maxLength={MAX_FAMILY_NAME_LENGTH + 1}
                onChange={(event) => { markDirty(); setFamilyName(event.target.value); setNameError(''); }}
                invalid={!!nameError}
                aria-invalid={!!nameError}
                aria-required="true"
                aria-describedby={nameError ? 'profile-family-name-error' : 'profile-family-name-help'}
                placeholder={`${targetPrinterModelName} Orca family`}
              />
            </FormField>
          </section>
        )}

        {stepIndex === 2 && selectedSource && (
          <section aria-labelledby="profile-family-step-nozzles" className="space-y-4">
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div>
                <h3 id="profile-family-step-nozzles" className="text-lg font-semibold text-pf-text-primary">Select nozzle sizes</h3>
                <p className="text-sm text-pf-text-secondary">Choose which shipped nozzle variants to generate for {selectedSource.modelName}.</p>
              </div>
              <div className="flex gap-2">
                <Button type="button" size="sm" variant="secondary" onClick={() => { markDirty(); setSelectedNozzles(new Set(selectedSource.availableNozzles)); }}>All</Button>
                <Button type="button" size="sm" variant="secondary" onClick={() => { markDirty(); setSelectedNozzles(new Set()); }}>None</Button>
              </div>
            </div>
            {selectedNozzles.size === 0 && <Alert type="warning">Select at least one nozzle size to continue.</Alert>}
            <div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
              {selectedSource.availableNozzles.map((diameter) => {
                const checked = selectedNozzles.has(diameter);
                return (
                  <label key={diameter} className={`flex items-center gap-3 rounded-lg border p-4 ${checked ? 'border-pf-accent bg-pf-accent/10' : 'border-pf-border bg-pf-bg-1'}`}>
                    <Checkbox
                      checked={checked}
                      onChange={() => {
                        markDirty();
                        setSelectedNozzles((current) => {
                          const next = new Set(current);
                          if (next.has(diameter)) next.delete(diameter);
                          else next.add(diameter);
                          return next;
                        });
                      }}
                      aria-label={`${formatNumber(diameter)} mm nozzle`}
                    />
                    <span className="font-medium text-pf-text-primary">{formatNumber(diameter)} mm nozzle</span>
                  </label>
                );
              })}
            </div>
          </section>
        )}

        {stepIndex === 3 && (
          <section aria-labelledby="profile-family-step-overrides" className="space-y-4">
            <div>
              <h3 id="profile-family-step-overrides" className="text-lg font-semibold text-pf-text-primary">Edit family-shared overrides</h3>
              <p className="text-sm text-pf-text-secondary">Set shared bed dimensions once; PrintFarmer writes OrcaSlicer's printable_area corner-list on submit.</p>
            </div>
            <div className="grid gap-4 sm:grid-cols-3">
              <FormField label="Bed X" htmlFor="profile-family-bed-x" helper="Millimeters." helperId="profile-family-bed-x-help">
                <Input id="profile-family-bed-x" type="number" min="1" value={bedX} onChange={(event) => { markDirty(); setBedX(event.target.value); }} aria-describedby="profile-family-bed-x-help" />
              </FormField>
              <FormField label="Bed Y" htmlFor="profile-family-bed-y" helper="Millimeters." helperId="profile-family-bed-y-help">
                <Input id="profile-family-bed-y" type="number" min="1" value={bedY} onChange={(event) => { markDirty(); setBedY(event.target.value); }} aria-describedby="profile-family-bed-y-help" />
              </FormField>
              <FormField label="Printable height" htmlFor="profile-family-height" helper="Millimeters." helperId="profile-family-height-help">
                <Input id="profile-family-height" type="number" min="1" value={printableHeight} onChange={(event) => { markDirty(); setPrintableHeight(event.target.value); }} aria-describedby="profile-family-height-help" />
              </FormField>
            </div>
            <details open={advancedOpen} onToggle={(event) => setAdvancedOpen((event.currentTarget as HTMLDetailsElement).open)} className="rounded-lg border border-pf-border bg-pf-bg-1 p-4">
              <summary className="cursor-pointer text-sm font-semibold text-pf-text-primary">Add advanced override</summary>
              <div className="mt-4 space-y-3">
                {advancedOverrides.map((row) => {
                  const error = advancedOverrideErrors.get(row.id);
                  return (
                    <div key={row.id} className="grid gap-2 rounded-md border border-pf-border p-3 md:grid-cols-[1fr_1fr_auto]">
                      <FormField label="Orca key" htmlFor={`advanced-key-${row.id}`} error={error} errorId={`advanced-key-${row.id}-error`}>
                        <Input
                          id={`advanced-key-${row.id}`}
                          value={row.key}
                          onChange={(event) => updateAdvancedOverride(row.id, { key: event.target.value })}
                          invalid={!!error}
                          aria-invalid={!!error}
                          aria-describedby={error ? `advanced-key-${row.id}-error` : undefined}
                          placeholder="slow_down_layer_time"
                        />
                      </FormField>
                      <FormField label="Value" htmlFor={`advanced-value-${row.id}`}>
                        <Input id={`advanced-value-${row.id}`} value={row.value} onChange={(event) => updateAdvancedOverride(row.id, { value: event.target.value })} placeholder="20" />
                      </FormField>
                      <div className="flex items-end">
                        <Button type="button" variant="secondary" onClick={() => removeAdvancedOverride(row.id)}>Remove</Button>
                      </div>
                    </div>
                  );
                })}
                <Button type="button" variant="secondary" onClick={addAdvancedOverride}>Add override</Button>
              </div>
            </details>
          </section>
        )}

        {stepIndex === 4 && selectedSource && (
          <section aria-labelledby="profile-family-step-review" className="space-y-4">
            <div>
              <h3 id="profile-family-step-review" className="text-lg font-semibold text-pf-text-primary">Review generated variants</h3>
              <p className="text-sm text-pf-text-secondary">Final names come from the backend; counts are estimated from the source hierarchy and may differ.</p>
            </div>
            {reviewAlert && <Alert type="error" title="Profile family cannot be created">{reviewAlert}</Alert>}
            <div className="space-y-2">
              {selectedNozzleList.map((diameter) => {
                const sourceProfile = sourceMachineByNozzle.get(Number(diameter.toFixed(3)));
                const compatibleName = sourceProfile?.name ?? '';
                const processCount = selectedSource.model.processProfiles?.filter(
                  (profile) => compatibleName !== ''
                    && profile.compatible_printers?.includes(compatibleName) === true
                ).length ?? 0;
                const filamentCount = selectedSource.model.filamentProfiles?.filter(
                  (profile) => profile.manufacturer?.toLowerCase() !== 'orcafilamentlibrary'
                    && compatibleName !== ''
                    && profile.compatible_printers?.includes(compatibleName) === true
                ).length ?? 0;
                return (
                  <Card key={diameter}>
                    <Card.Body className="grid gap-2 md:grid-cols-4">
                      <div>
                        <div className="text-xs text-pf-text-secondary">Variant preview</div>
                        <div className="font-medium text-pf-text-primary">{familyName.trim() || 'Family'} {formatNumber(diameter)} nozzle</div>
                      </div>
                      <div>
                        <div className="text-xs text-pf-text-secondary">Source preset</div>
                        <div className="text-sm text-pf-text-primary">{String(sourceProfile?.name ?? 'No matching source preset')}</div>
                      </div>
                      <div>
                        <div className="text-xs text-pf-text-secondary">Derived processes</div>
                        <div className="text-sm text-pf-text-primary">{processCount} estimated</div>
                      </div>
                      <div>
                        <div className="text-xs text-pf-text-secondary">Derived filaments</div>
                        <div className="text-sm text-pf-text-primary">{filamentCount} estimated</div>
                      </div>
                    </Card.Body>
                  </Card>
                );
              })}
            </div>
            <Card>
              <Card.Body>
                <h4 className="text-sm font-semibold text-pf-text-primary">Family-shared overrides</h4>
                <dl className="mt-2 grid gap-2 text-sm sm:grid-cols-2">
                  {Object.entries(familyOverrides).map(([key, value]) => (
                    <div key={key}>
                      <dt className="font-medium text-pf-text-secondary">{String(key)}</dt>
                      <dd className="break-all text-pf-text-primary">{renderUnknown(value)}</dd>
                    </div>
                  ))}
                </dl>
              </Card.Body>
            </Card>
          </section>
        )}

        {stepIndex === 5 && selectedSource && (
          <section aria-labelledby="profile-family-step-confirm" className="space-y-4">
            <div>
              <h3 id="profile-family-step-confirm" className="text-lg font-semibold text-pf-text-primary">Confirm profile family creation</h3>
              <p className="text-sm text-pf-text-secondary">PrintFarmer will atomically clone {selectedNozzleList.length} machine variant(s) from {selectedSource.manufacturer} {selectedSource.modelName} for {targetPrinterModelName}.</p>
            </div>
            <Alert type="info">This sends one request to create the family and all generated profiles together.</Alert>
            {defaultNozzleDiameter !== undefined && (
              <p className="text-xs text-pf-text-secondary">After creation, PrintFarmer will prefer the {formatNumber(defaultNozzleDiameter)} mm variant if the backend returns one.</p>
            )}
          </section>
        )}
      </div>
    </Modal>
  );
}
