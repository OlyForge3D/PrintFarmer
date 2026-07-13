/**
 * Harvest Job Dialog (#722).
 *
 * Accessible multi-step flow that runs the printed-parts harvest action
 * for a completed job. States are driven by the server's canonical
 * ProblemDetails responses from #741 (wrongBin, partMappingRequired,
 * featureDisabled), plus idempotent replay semantics.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Alert } from '@/common/components/ui/Alert';
import { Badge } from '@/common/components/ui/Badge';
import { Button } from '@/common/components/ui/Button';
import { FormField } from '@/common/components/ui/FormField';
import { Input } from '@/common/components/ui/Input';
import { NumberStepper } from '@/common/components/ui/NumberStepper';
import { Textarea } from '@/common/components/ui/Textarea';
import { Checkbox } from '@/common/components/ui/Checkbox';
import { toast } from 'sonner';
import type {
  HarvestJobResponse,
  HarvestOutputRequestItem,
  HarvestJobRequest,
  PartInventoryResponse,
  WrongBinMismatchResponse,
  PartMappingRequiredDetails,
} from '@/types/parts-inventory';
import { listParts, generateHarvestOperationKey, HarvestServiceError } from '@/services/partsHarvest';
import { useHarvestJob } from '@/features/harvest/hooks/useHarvestJob';

const MAX_HARVEST_QUANTITY_PER_SKU = 10_000;
const MAX_OVERRIDE_REASON_LENGTH = 1000;

export interface HarvestJobDialogProps {
  /** Whether the dialog is open. */
  isOpen: boolean;
  /** Callback when the dialog should close (Cancel, ESC, or close button). */
  onClose: () => void;
  /** The completed job being harvested. */
  job: {
    id: string;
    name: string;
    /** Previous harvest timestamp; when set, dialog opens in already-harvested mode. */
    harvestedAt?: string | null;
  };
  /** Optional callback fired after a successful (or replayed) harvest. */
  onHarvested?: (response: HarvestJobResponse) => void;
  /**
   * Optional callback fired when the dialog closes *after* at least one
   * successful harvest occurred during this session. Parents use this to run
   * an expensive refresh (e.g. reloading a history list) only once the user
   * has finished reading the success/output details — the immediate refresh
   * would otherwise unmount this dialog before it can be read (#722 H5).
   */
  onCloseAfterSuccess?: () => void;
}

type DialogStep =
  | { kind: 'preview' }
  | { kind: 'wrongBin'; mismatches: WrongBinMismatchResponse[]; message: string }
  | { kind: 'partMappingRequired'; details: PartMappingRequiredDetails; message: string }
  | { kind: 'featureDisabled'; message: string }
  | { kind: 'success'; response: HarvestJobResponse }
  | { kind: 'error'; message: string };

interface ManualOutputRow {
  id: string;
  sku: string;
  quantity: number;
  binCode: string;
}

/** A single per-SKU destination-bin assignment collected in the preview step. */
interface PreviewBinRow {
  id: string;
  sku: string;
  binCode: string;
}

function newRowId(): string {
  return `row-${Math.random().toString(36).slice(2, 10)}`;
}

export function HarvestJobDialog({
  isOpen,
  onClose,
  job,
  onHarvested,
  onCloseAfterSuccess,
}: HarvestJobDialogProps) {
  return (
    <HarvestJobDialogShell
      isOpen={isOpen}
      onClose={onClose}
      job={job}
      onHarvested={onHarvested}
      onCloseAfterSuccess={onCloseAfterSuccess}
    />
  );
}

/**
 * Outer shell — hosts the Modal chrome and mounts the stateful Inner
 * only while `isOpen`, so all local state resets to defaults on the next
 * open (no setState-in-effect reset, and no QueryClient dependency for
 * tests that never open the dialog).
 */
function HarvestJobDialogShell({
  isOpen,
  onClose,
  job,
  onHarvested,
  onCloseAfterSuccess,
}: HarvestJobDialogProps) {
  if (!isOpen) return null;
  return (
    <HarvestJobDialogInner
      isOpen={isOpen}
      onClose={onClose}
      job={job}
      onHarvested={onHarvested}
      onCloseAfterSuccess={onCloseAfterSuccess}
    />
  );
}

/**
 * Harvest a completed print job. Renders inline error/mapping/wrong-bin
 * recovery states inside a single accessible dialog. State is initialized
 * fresh each time this component mounts (i.e., each time the shell opens).
 */
function HarvestJobDialogInner({
  isOpen,
  onClose,
  job,
  onHarvested,
  onCloseAfterSuccess,
}: HarvestJobDialogProps) {
  const [step, setStep] = useState<DialogStep>({ kind: 'preview' });
  const [sharedBinCode, setSharedBinCode] = useState<string>('');
  const [uniformQuantity, setUniformQuantity] = useState<number>(1);
  const [useUniformQuantity, setUseUniformQuantity] = useState<boolean>(false);
  // UX gate: we require an audit reason whenever the operator overrides the
  // completed-copies count, even though the backend permits mapped quantityOverride
  // without one. Explicit outputs and wrong-bin overrides are backend-required.
  const [copiesOverrideReason, setCopiesOverrideReason] = useState<string>('');
  // Optional per-SKU destination bins collected via the preview disclosure.
  const [assignPerSkuBins, setAssignPerSkuBins] = useState<boolean>(false);
  const [previewBinRows, setPreviewBinRows] = useState<PreviewBinRow[]>([]);
  const [manualRows, setManualRows] = useState<ManualOutputRow[]>([]);
  const [overrideReason, setOverrideReason] = useState<string>('');
  // Audit reason required by the backend for explicit manual `outputs[]`.
  const [manualReason, setManualReason] = useState<string>('');
  const [parts, setParts] = useState<PartInventoryResponse[]>([]);
  const [partsLoadError, setPartsLoadError] = useState<string | null>(null);
  // One operationKey per dialog open — replay of the same key is idempotent
  // server-side so retries from wrong-bin / mapping fallback do not double-count.
  const operationKeyRef = useRef<string>(generateHarvestOperationKey());
  const lastRequestRef = useRef<HarvestJobRequest | null>(null);
  // True once any harvest in this dialog session has succeeded; drives the
  // deferred parent refresh on close (#722 H5).
  const harvestSucceededRef = useRef<boolean>(false);

  const mutation = useHarvestJob();

  const alreadyHarvested = Boolean(job.harvestedAt);

  // Fetch SKUs for the manual/fallback rows. Feature-disabled surfaces
  // through the ProblemDetails handler on submit; here we just record the
  // error so the dialog can still render.
  useEffect(() => {
    if (!isOpen) return;
    let cancelled = false;
    listParts()
      .then((rows) => {
        if (!cancelled) setParts(rows);
      })
      .catch((error: unknown) => {
        if (cancelled) return;
        const message =
          error instanceof HarvestServiceError
            ? error.info.message
            : error instanceof Error
              ? error.message
              : 'Unable to load SKUs.';
        setPartsLoadError(message);
      });
    return () => {
      cancelled = true;
    };
  }, [isOpen]);

  const dialogTitle = useMemo(() => {
    if (alreadyHarvested && step.kind === 'preview') {
      return `Already harvested — ${job.name}`;
    }
    switch (step.kind) {
      case 'success':
        return step.response.alreadyHarvested
          ? `Already harvested — ${job.name}`
          : `Harvest complete — ${job.name}`;
      case 'wrongBin':
        return `Wrong bin — ${job.name}`;
      case 'partMappingRequired':
        return `Enter outputs manually — ${job.name}`;
      case 'featureDisabled':
        return `Printed-parts inventory unavailable`;
      case 'error':
        return `Harvest failed — ${job.name}`;
      default:
        return `Harvest — ${job.name}`;
    }
  }, [alreadyHarvested, step, job.name]);

  const runHarvest = useCallback(
    (request: HarvestJobRequest) => {
      const augmented: HarvestJobRequest = {
        ...request,
        operationKey: request.operationKey ?? operationKeyRef.current,
      };
      lastRequestRef.current = augmented;
      mutation.mutate(
        { jobId: job.id, request: augmented },
        {
          onSuccess: (response) => {
            harvestSucceededRef.current = true;
            setStep({ kind: 'success', response });
            if (!response.alreadyHarvested) {
              toast.success('Harvest complete.');
            }
            onHarvested?.(response);
          },
          onError: (error: HarvestServiceError) => {
            const info = error.info;
            switch (info.kind) {
              case 'wrongBin':
                setStep({ kind: 'wrongBin', mismatches: info.mismatches, message: info.message });
                break;
              case 'partMappingRequired':
                setStep({
                  kind: 'partMappingRequired',
                  details: info.details,
                  message: info.message,
                });
                if (manualRows.length === 0) {
                  setManualRows([{ id: newRowId(), sku: '', quantity: 1, binCode: '' }]);
                }
                break;
              case 'featureDisabled':
                setStep({ kind: 'featureDisabled', message: info.message });
                break;
              default:
                setStep({ kind: 'error', message: info.message });
            }
          },
        },
      );
    },
    [job.id, manualRows.length, mutation, onHarvested],
  );

  const submitPreview = useCallback(() => {
    const request: HarvestJobRequest = {};
    const bin = sharedBinCode.trim();
    if (bin) request.binCode = bin;
    if (useUniformQuantity) {
      // `quantityOverride` is a copy multiplier, not a per-SKU final quantity.
      // Requiring its audit reason is an intentionally stricter frontend UX gate;
      // the backend requires reasons only for explicit outputs and wrong-bin overrides.
      request.quantityOverride = uniformQuantity;
      request.overrideReason = copiesOverrideReason.trim();
    }
    // Per-SKU destination bins collected via the "Assign bins per SKU"
    // disclosure override the shared bin for the listed SKUs.
    if (assignPerSkuBins) {
      const outputBins = previewBinRows
        .filter((r) => r.sku.trim() !== '' && r.binCode.trim() !== '')
        .map((r) => ({ partSku: r.sku.trim(), binCode: r.binCode.trim() }));
      if (outputBins.length > 0) request.outputBins = outputBins;
    }
    runHarvest(request);
  }, [
    assignPerSkuBins,
    copiesOverrideReason,
    previewBinRows,
    runHarvest,
    sharedBinCode,
    uniformQuantity,
    useUniformQuantity,
  ]);

  const submitWrongBinOverride = useCallback(() => {
    const previous = lastRequestRef.current ?? {};
    runHarvest({
      ...previous,
      allowWrongBin: true,
      overrideReason: overrideReason.trim(),
    });
  }, [overrideReason, runHarvest]);

  const submitManualOutputs = useCallback(() => {
    const outputs: HarvestOutputRequestItem[] = manualRows
      .filter((r) => r.sku.trim() !== '' && r.quantity > 0)
      .map((r) => ({ sku: r.sku.trim(), quantity: r.quantity }));
    const outputBins = manualRows
      .filter((r) => r.sku.trim() !== '' && r.binCode.trim() !== '')
      .map((r) => ({ partSku: r.sku.trim(), binCode: r.binCode.trim() }));
    const bin = sharedBinCode.trim();
    // Explicit `outputs[]` always require an audit reason server-side.
    const request: HarvestJobRequest = { outputs, overrideReason: manualReason.trim() };
    if (outputBins.length > 0) request.outputBins = outputBins;
    if (bin) request.binCode = bin;
    runHarvest(request);
  }, [manualReason, manualRows, runHarvest, sharedBinCode]);

  // Retry from the generic error step must replay the exact failed request
  // (same operationKey) so a transient network failure during a manual or
  // override submit does not silently drop the user's inputs (#722 B2/H7).
  const retryLastRequest = useCallback(() => {
    const previous = lastRequestRef.current;
    if (previous) {
      runHarvest(previous);
    } else {
      submitPreview();
    }
  }, [runHarvest, submitPreview]);

  const closeAndReset = useCallback(() => {
    // Defer the parent's expensive refresh until the dialog is actually
    // closing, so the success/output details stay readable (#722 H5).
    if (harvestSucceededRef.current) {
      onCloseAfterSuccess?.();
    }
    onClose();
  }, [onClose, onCloseAfterSuccess]);

  // Move focus into each recovery step's primary control after an async step
  // transition, so keyboard users are not dropped onto <body> (#722 V1).
  useEffect(() => {
    if (step.kind === 'wrongBin') {
      document.getElementById('harvest-override-reason')?.focus();
    } else if (step.kind === 'partMappingRequired') {
      const firstSku = document.querySelector<HTMLElement>(
        '[data-testid="harvest-manual-row"] input',
      );
      firstSku?.focus();
    }
  }, [step.kind]);

  const isBusy = mutation.isPending;

  // Trimmed-reason validity gates (shared by render + footer buttons).
  const overrideReasonMissing = overrideReason.trim().length === 0;
  const manualReasonMissing = manualReason.trim().length === 0;
  const copiesReasonMissing = useUniformQuantity && copiesOverrideReason.trim().length === 0;

  // ----- Render helpers --------------------------------------------------

  const renderAlreadyHarvestedPreview = () => (
    <div className="space-y-3" data-testid="harvest-already-harvested">
      <Alert type="info">
        <span>
          This job was already harvested{' '}
          {job.harvestedAt ? (
            <>on <time dateTime={job.harvestedAt}>{new Date(job.harvestedAt).toLocaleString()}</time></>
          ) : (
            'previously'
          )}
          . Printed-part stock has already been credited.
        </span>
      </Alert>
      <p className="text-sm text-pf-text-secondary">
        You can safely close this dialog. Harvesting again with the same operation key
        will not double-count stock.
      </p>
    </div>
  );

  const renderPreview = () => (
    <div className="space-y-4" data-testid="harvest-preview">
      <p className="text-sm text-pf-text-secondary">
        Confirm printed-part outputs for <strong>{job.name}</strong>. Mapped SKUs
        and quantities come from the job's output mapping; corrections below are
        optional.
      </p>

      {partsLoadError && (
        <Alert type="warning">
          Could not preload SKU list ({partsLoadError}). You can still harvest
          using the job's mapped values.
        </Alert>
      )}

      <FormField label="Destination bin (optional)" htmlFor="harvest-shared-bin">
        <Input
          id="harvest-shared-bin"
          value={sharedBinCode}
          onChange={(e) => setSharedBinCode(e.target.value)}
          placeholder="Leave blank to use each SKU's default bin"
          aria-describedby="harvest-shared-bin-help"
        />
        <p id="harvest-shared-bin-help" className="mt-1 text-xs text-pf-text-secondary">
          Applied to every output. Mismatches against the SKU's default bin will
          be flagged before stock is credited.
        </p>
      </FormField>

      <div className="space-y-2">
        <Checkbox
          checked={useUniformQuantity}
          onChange={(e) => setUseUniformQuantity(e.target.checked)}
          label="Override completed copies (default: job copies)"
          id="harvest-uniform-qty-toggle"
        />
        {useUniformQuantity && (
          <div className="space-y-3 rounded-sm border border-pf-border-light p-3">
            <FormField label="Completed copies" htmlFor="harvest-uniform-qty">
              <NumberStepper
                id="harvest-uniform-qty"
                value={uniformQuantity}
                onChange={setUniformQuantity}
                min={1}
                max={MAX_HARVEST_QUANTITY_PER_SKU}
                step={1}
              />
              <p id="harvest-uniform-qty-help" className="mt-1 text-xs text-pf-text-secondary">
                Multiplied by each mapped SKU's per-print quantity to compute stock added.
              </p>
            </FormField>
            <FormField
              label="Override reason (required)"
              htmlFor="harvest-copies-reason"
              helper={
                <span id="harvest-copies-reason-counter">
                  {copiesOverrideReason.length}/{MAX_OVERRIDE_REASON_LENGTH}
                </span>
              }
            >
              <Textarea
                id="harvest-copies-reason"
                value={copiesOverrideReason}
                onChange={(e) => setCopiesOverrideReason(e.target.value)}
                rows={2}
                maxLength={MAX_OVERRIDE_REASON_LENGTH}
                placeholder="Explain why the completed copy count differs from the job (audited)."
                invalid={copiesReasonMissing}
                aria-invalid={copiesReasonMissing}
                aria-describedby={
                  copiesReasonMissing
                    ? 'harvest-copies-reason-counter harvest-copies-reason-error'
                    : 'harvest-copies-reason-counter'
                }
              />
              {copiesReasonMissing && (
                <p id="harvest-copies-reason-error" className="text-xs text-pf-error-text" role="alert">
                  A reason is required when overriding completed copies.
                </p>
              )}
            </FormField>
          </div>
        )}
      </div>

      {/*
        H3: there is no "GET expected outputs for a job" endpoint yet, so the
        safest UX for multi-SKU plates is an opt-in disclosure that lets the
        operator list SKU → bin pairs manually (suggested from the inventory
        roster) rather than always forcing explicit outputs. The shared bin
        above remains the default; these rows override it per SKU.
      */}
      <div className="space-y-2">
        <Checkbox
          checked={assignPerSkuBins}
          onChange={(e) => {
            const on = e.target.checked;
            setAssignPerSkuBins(on);
            if (on && previewBinRows.length === 0) {
              setPreviewBinRows([{ id: newRowId(), sku: '', binCode: '' }]);
            }
          }}
          label="Assign bins per SKU"
          id="harvest-assign-bins-toggle"
        />
        {assignPerSkuBins && (
          <div role="group" aria-label="Per-SKU destination bins" className="space-y-2">
            <p className="text-xs text-pf-text-secondary">
              Overrides the shared bin for the SKUs listed here. Enter each SKU
              and its destination bin; the SKU field suggests known parts from
              the inventory roster.
            </p>
            {previewBinRows.map((row, index) => (
              <div
                key={row.id}
                className="grid grid-cols-1 gap-2 sm:grid-cols-[1fr_1fr_auto] sm:items-end"
                data-testid="harvest-bin-row"
              >
                <FormField label={`SKU #${index + 1}`} htmlFor={`harvest-binrow-sku-${row.id}`}>
                  <Input
                    id={`harvest-binrow-sku-${row.id}`}
                    list={parts.length > 0 ? 'harvest-parts-datalist' : undefined}
                    value={row.sku}
                    onChange={(e) =>
                      setPreviewBinRows((rows) =>
                        rows.map((r) => (r.id === row.id ? { ...r, sku: e.target.value } : r)),
                      )
                    }
                    placeholder="SKU code"
                  />
                </FormField>
                <FormField label={`Bin #${index + 1}`} htmlFor={`harvest-binrow-bin-${row.id}`}>
                  <Input
                    id={`harvest-binrow-bin-${row.id}`}
                    value={row.binCode}
                    onChange={(e) =>
                      setPreviewBinRows((rows) =>
                        rows.map((r) => (r.id === row.id ? { ...r, binCode: e.target.value } : r)),
                      )
                    }
                    placeholder="Bin code"
                  />
                </FormField>
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() =>
                    setPreviewBinRows((rows) => rows.filter((r) => r.id !== row.id))
                  }
                  aria-label={`Remove bin assignment ${index + 1}`}
                  disabled={previewBinRows.length === 1}
                >
                  ✕
                </Button>
              </div>
            ))}
            <Button
              variant="secondary"
              onClick={() =>
                setPreviewBinRows((rows) => [...rows, { id: newRowId(), sku: '', binCode: '' }])
              }
            >
              + Add SKU bin
            </Button>
          </div>
        )}
      </div>

      {parts.length > 0 && (
        <datalist id="harvest-parts-datalist">
          {parts.map((p) => (
            <option key={p.id} value={p.sku}>
              {p.name}
            </option>
          ))}
        </datalist>
      )}
    </div>
  );

  const renderWrongBin = () => {
    if (step.kind !== 'wrongBin') return null;
    return (
      <div className="space-y-4" data-testid="harvest-wrong-bin">
        <Alert type="warning">
          <div className="space-y-1">
            <p className="font-medium">Bin mismatch detected.</p>
            <p className="text-sm">{step.message}</p>
          </div>
        </Alert>
        <ul className="space-y-1 text-sm">
          {step.mismatches.map((m) => (
            <li key={m.partSku} className="flex items-center gap-2">
              <span aria-hidden className="text-pf-warning">⚠</span>
              <span>
                SKU <strong>{m.partSku}</strong>: expected bin{' '}
                <code>{m.expectedBinCode ?? '—'}</code>, scanned{' '}
                <code>{m.scannedBinCode}</code>.
              </span>
            </li>
          ))}
        </ul>
        <FormField
          label="Override reason (required)"
          htmlFor="harvest-override-reason"
          helper={
            <span id="harvest-override-reason-counter">
              {overrideReason.length}/{MAX_OVERRIDE_REASON_LENGTH}
            </span>
          }
        >
          <Textarea
            id="harvest-override-reason"
            value={overrideReason}
            onChange={(e) => setOverrideReason(e.target.value)}
            rows={3}
            maxLength={MAX_OVERRIDE_REASON_LENGTH}
            placeholder="Explain why the scanned bin is acceptable (audited)."
            invalid={overrideReasonMissing}
            aria-invalid={overrideReasonMissing}
            aria-describedby={
              overrideReasonMissing
                ? 'harvest-override-reason-counter harvest-override-reason-error'
                : 'harvest-override-reason-counter'
            }
          />
          {overrideReasonMissing && (
            <p id="harvest-override-reason-error" className="text-xs text-pf-error-text" role="alert">
              A reason is required to override the wrong-bin check.
            </p>
          )}
        </FormField>
      </div>
    );
  };

  const renderMappingRequired = () => {
    if (step.kind !== 'partMappingRequired') return null;
    const addRow = () =>
      setManualRows((rows) => [...rows, { id: newRowId(), sku: '', quantity: 1, binCode: '' }]);
    const removeRow = (id: string) =>
      setManualRows((rows) => rows.filter((r) => r.id !== id));
    const updateRow = (id: string, patch: Partial<ManualOutputRow>) =>
      setManualRows((rows) => rows.map((r) => (r.id === id ? { ...r, ...patch } : r)));

    return (
      <div className="space-y-4" data-testid="harvest-mapping-required">
        <Alert type="info">
          <div className="space-y-1">
            <p className="font-medium">Printed-part mapping required.</p>
            <p className="text-sm">{step.details.guidance || step.message}</p>
          </div>
        </Alert>

        <div role="group" aria-label="Manual outputs" className="space-y-3">
          {manualRows.map((row, index) => (
            <div
              key={row.id}
              className="grid grid-cols-1 gap-3 sm:grid-cols-[1fr_auto_auto_auto] sm:gap-2 sm:items-end"
              data-testid="harvest-manual-row"
            >
              <FormField label={`SKU #${index + 1}`} htmlFor={`harvest-sku-${row.id}`}>
                <Input
                  id={`harvest-sku-${row.id}`}
                  list={parts.length > 0 ? 'harvest-parts-datalist' : undefined}
                  value={row.sku}
                  onChange={(e) => updateRow(row.id, { sku: e.target.value })}
                  placeholder="SKU code"
                />
              </FormField>
              <FormField label="Quantity" htmlFor={`harvest-qty-${row.id}`}>
                <NumberStepper
                  id={`harvest-qty-${row.id}`}
                  value={row.quantity}
                  onChange={(v) => updateRow(row.id, { quantity: v })}
                  min={1}
                  max={MAX_HARVEST_QUANTITY_PER_SKU}
                  step={1}
                />
              </FormField>
              <FormField label="Bin (optional)" htmlFor={`harvest-bin-${row.id}`}>
                <Input
                  id={`harvest-bin-${row.id}`}
                  value={row.binCode}
                  onChange={(e) => updateRow(row.id, { binCode: e.target.value })}
                  placeholder="Bin code"
                />
              </FormField>
              <Button
                variant="ghost"
                size="sm"
                onClick={() => removeRow(row.id)}
                aria-label={`Remove row ${index + 1}`}
                disabled={manualRows.length === 1}
              >
                ✕
              </Button>
            </div>
          ))}
          {parts.length > 0 && (
            <datalist id="harvest-parts-datalist">
              {parts.map((p) => (
                <option key={p.id} value={p.sku}>
                  {p.name}
                </option>
              ))}
            </datalist>
          )}
          <Button variant="secondary" onClick={addRow}>
            + Add another SKU
          </Button>
        </div>

        <FormField label="Shared destination bin (optional)" htmlFor="harvest-manual-shared-bin">
          <Input
            id="harvest-manual-shared-bin"
            value={sharedBinCode}
            onChange={(e) => setSharedBinCode(e.target.value)}
            placeholder="Applied when a row has no bin"
          />
        </FormField>

        <FormField
          label="Audit reason for manual outputs (required)"
          htmlFor="harvest-manual-reason"
          helper={
            <span id="harvest-manual-reason-counter">
              {manualReason.length}/{MAX_OVERRIDE_REASON_LENGTH}
            </span>
          }
        >
          <Textarea
            id="harvest-manual-reason"
            value={manualReason}
            onChange={(e) => setManualReason(e.target.value)}
            rows={2}
            maxLength={MAX_OVERRIDE_REASON_LENGTH}
            placeholder="Explain why outputs are being entered manually (audited)."
            invalid={manualReasonMissing}
            aria-invalid={manualReasonMissing}
            aria-describedby={
              manualReasonMissing
                ? 'harvest-manual-reason-counter harvest-manual-reason-error'
                : 'harvest-manual-reason-counter'
            }
          />
          {manualReasonMissing && (
            <p id="harvest-manual-reason-error" className="text-xs text-pf-error-text" role="alert">
              A reason is required when entering outputs manually.
            </p>
          )}
        </FormField>
      </div>
    );
  };

  const renderFeatureDisabled = () => {
    if (step.kind !== 'featureDisabled') return null;
    return (
      <div className="space-y-3" data-testid="harvest-feature-disabled">
        <Alert type="info">
          <div className="space-y-1">
            <p className="font-medium">Printed-parts inventory is not enabled.</p>
            <p className="text-sm">{step.message}</p>
          </div>
        </Alert>
        <p className="text-sm text-pf-text-secondary">
          Ask an administrator to enable the printed-parts inventory feature to
          start recording harvest outputs.
        </p>
      </div>
    );
  };

  const renderSuccess = () => {
    if (step.kind !== 'success') return null;
    const { response } = step;
    return (
      <div className="space-y-4" data-testid="harvest-success">
        <Alert type={response.alreadyHarvested ? 'info' : 'success'}>
          <div className="space-y-1">
            <p className="font-medium">
              {response.alreadyHarvested
                ? 'This job was already harvested. Stock was not changed.'
                : 'Harvest complete. Printed-part stock updated.'}
            </p>
            <p className="text-sm">
              Harvested at{' '}
              <time dateTime={response.harvestedAt}>
                {new Date(response.harvestedAt).toLocaleString()}
              </time>
              {response.binCode ? <> · destination bin <code>{response.binCode}</code></> : null}.
            </p>
          </div>
        </Alert>
        {response.outputs.length > 0 && (
          <div>
            <p className="text-sm font-medium mb-2">Outputs</p>
            <ul className="space-y-1 text-sm">
              {response.outputs.map((o) => (
                <li key={`${o.sequence}-${o.partSku}`} className="flex items-center gap-2">
                  <Badge variant="default">×{o.quantity}</Badge>
                  <span>
                    <strong>{o.partSku}</strong> → bin <code>{o.actualBinCode}</code>
                    {o.overrideApplied ? ' (override)' : ''}
                  </span>
                </li>
              ))}
            </ul>
          </div>
        )}
        {response.adjustments.length > 0 && (
          <div>
            <p className="text-sm font-medium mb-2">Ledger adjustments</p>
            <ul className="space-y-1 text-sm">
              {response.adjustments.map((a) => (
                <li key={a.id}>
                  <strong>{a.sku}</strong>: {a.delta >= 0 ? '+' : ''}
                  {a.delta} (new balance {a.resultingBalance})
                  {a.binCode ? <> · bin <code>{a.binCode}</code></> : null}
                </li>
              ))}
            </ul>
          </div>
        )}
      </div>
    );
  };

  const renderError = () => {
    if (step.kind !== 'error') return null;
    return (
      <div className="space-y-3" data-testid="harvest-error">
        <Alert type="error">
          <div className="space-y-1">
            <p className="font-medium">Harvest failed.</p>
            <p className="text-sm">{step.message}</p>
          </div>
        </Alert>
      </div>
    );
  };

  // ----- Footer buttons per step ---------------------------------------

  const renderFooter = () => {
    if (alreadyHarvested && step.kind === 'preview') {
      return (
        <div className="flex justify-end gap-2">
          <Button variant="primary" onClick={closeAndReset}>Close</Button>
        </div>
      );
    }
    switch (step.kind) {
      case 'preview':
        return (
          <div className="flex justify-end gap-2">
            <Button variant="secondary" onClick={closeAndReset} disabled={isBusy}>
              Cancel
            </Button>
            <Button variant="primary" onClick={submitPreview} disabled={isBusy || copiesReasonMissing}>
              {isBusy ? 'Harvesting…' : 'Confirm harvest'}
            </Button>
          </div>
        );
      case 'wrongBin':
        return (
          <div className="flex justify-end gap-2">
            <Button variant="secondary" onClick={closeAndReset} disabled={isBusy}>
              Cancel
            </Button>
            <Button
              variant="danger"
              onClick={submitWrongBinOverride}
              disabled={isBusy || overrideReasonMissing}
            >
              <span aria-hidden className="mr-1">⚠</span>
              {isBusy ? 'Retrying…' : 'Override & harvest'}
            </Button>
          </div>
        );
      case 'partMappingRequired':
        return (
          <div className="flex justify-end gap-2">
            <Button variant="secondary" onClick={closeAndReset} disabled={isBusy}>
              Cancel
            </Button>
            <Button
              variant="primary"
              onClick={submitManualOutputs}
              disabled={
                isBusy ||
                manualReasonMissing ||
                manualRows.filter((r) => r.sku.trim() !== '' && r.quantity > 0).length === 0
              }
            >
              {isBusy ? 'Harvesting…' : 'Confirm manual harvest'}
            </Button>
          </div>
        );
      case 'featureDisabled':
        return (
          <div className="flex justify-end">
            <Button variant="primary" onClick={closeAndReset}>Close</Button>
          </div>
        );
      case 'success':
        return (
          <div className="flex justify-end">
            <Button variant="primary" onClick={closeAndReset}>Done</Button>
          </div>
        );
      case 'error':
        return (
          <div className="flex justify-end gap-2">
            <Button variant="secondary" onClick={closeAndReset}>Close</Button>
            <Button variant="primary" onClick={retryLastRequest} disabled={isBusy}>
              {isBusy ? 'Retrying…' : 'Retry'}
            </Button>
          </div>
        );
    }
  };

  return (
    <Modal
      isOpen={isOpen}
      onClose={closeAndReset}
      title={dialogTitle}
      size="xl"
      isDisabled={isBusy}
      closeOnBackdrop={false}
      footer={renderFooter()}
    >
      {alreadyHarvested && step.kind === 'preview' ? renderAlreadyHarvestedPreview() : null}
      {(!alreadyHarvested || step.kind !== 'preview') && step.kind === 'preview' ? renderPreview() : null}
      {step.kind === 'wrongBin' ? renderWrongBin() : null}
      {step.kind === 'partMappingRequired' ? renderMappingRequired() : null}
      {step.kind === 'featureDisabled' ? renderFeatureDisabled() : null}
      {step.kind === 'success' ? renderSuccess() : null}
      {step.kind === 'error' ? renderError() : null}
    </Modal>
  );
}
