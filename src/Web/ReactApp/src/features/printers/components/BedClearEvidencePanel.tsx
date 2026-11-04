import { useEffect, useId, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Alert, Badge, Button, Card, Spinner } from '@/common/components/ui';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { cameraService } from '@/services/cameraService';
import type { BedClearEvaluationDto, BedClearGroundTruth } from '@/types/api';

interface BedClearEvidencePanelProps {
  printerId: string;
}

const evaluationsKey = (printerId: string) => ['bed-clear-evaluations', printerId] as const;
const metricsKey = (printerId: string) => ['bed-clear-detector-metrics', printerId] as const;

function formatRate(rate: number | null): string {
  return rate === null ? 'Unavailable' : `${(rate * 100).toFixed(1)}%`;
}

function formatReason(reasonCode: string | null): string | null {
  return reasonCode?.replaceAll('_', ' ') ?? null;
}

function EvaluationImage({ evaluationId, onReady }: { evaluationId: string; onReady: (ready: boolean) => void }) {
  const [decodeFailed, setDecodeFailed] = useState(false);
  const imageQuery = useQuery({
    queryKey: ['bed-clear-evaluation-image', evaluationId],
    queryFn: () => cameraService.getBedClearEvaluationImage(evaluationId),
    staleTime: 300_000,
  });
  const imageUrl = useMemo(
    () => imageQuery.data ? URL.createObjectURL(imageQuery.data) : null,
    [imageQuery.data]
  );

  useEffect(() => () => {
    if (imageUrl) URL.revokeObjectURL(imageUrl);
  }, [imageUrl]);

  if (imageQuery.isLoading) return <div className="text-sm text-pf-text-secondary">Loading sample image…</div>;
  if (imageQuery.isError || !imageUrl || decodeFailed) return <div className="text-sm text-pf-text-secondary">Sample image unavailable.</div>;
  return <img
    className="max-h-64 w-full rounded-md object-contain"
    src={imageUrl}
    alt="Camera image captured for bed-clear evaluation"
    onLoad={() => onReady(true)}
    onError={() => { setDecodeFailed(true); onReady(false); }}
  />;
}

function EvaluationCard({
  evaluation,
  canLabel,
  canDelete,
  onLabel,
  onDelete,
  isLabeling,
  isDeleting,
}: {
  evaluation: BedClearEvaluationDto;
  canLabel: boolean;
  canDelete: boolean;
  onLabel: (evaluationId: string, label: BedClearGroundTruth) => void;
  onDelete: (evaluation: BedClearEvaluationDto) => void;
  isLabeling: boolean;
  isDeleting: boolean;
}) {
  const [showImage, setShowImage] = useState(false);
  const [imageReady, setImageReady] = useState(false);

  return (
    <Card>
      <Card.Body className="space-y-3">
        <div className="flex flex-wrap items-center gap-2">
          {evaluation.groundTruth ? (
            <Badge variant={evaluation.verdict === 'Clear' ? 'success' : evaluation.verdict === 'Occupied' ? 'warning' : 'default'}>
              Detector: {evaluation.verdict}
            </Badge>
          ) : <span className="text-xs text-pf-text-secondary">Detector result hidden until independent labeling.</span>}
          {evaluation.isActiveReference && <Badge variant="primary">Active empty-bed reference</Badge>}
          {evaluation.state === 'Pending' || evaluation.state === 'Processing'
            ? <Badge variant="default">{evaluation.state}</Badge>
            : null}
          {evaluation.groundTruth && evaluation.score !== null && <span className="text-xs text-pf-text-secondary">Similarity {(evaluation.score * 100).toFixed(1)}% (not confidence)</span>}
        </div>
        {evaluation.reasonCode && (evaluation.groundTruth || !evaluation.hasImage) && <p className="text-sm text-pf-text-secondary">Reason: {formatReason(evaluation.reasonCode)}</p>}
        {evaluation.hasImage
          ? <>
            <Button type="button" size="sm" variant="subtle" aria-expanded={showImage} onClick={() => {
              setShowImage(!showImage);
              setImageReady(false);
            }}>
              {showImage ? 'Hide image' : 'Review image'}
            </Button>
            {showImage && <EvaluationImage evaluationId={evaluation.id} onReady={setImageReady} />}
          </>
          : <p className="text-sm text-pf-text-secondary">No usable image was captured.</p>}
        <div className="text-xs text-pf-text-secondary">
          <p>{evaluation.sourceAcknowledgementId ? 'Acknowledged' : 'Shadow sample requested'} {new Date(evaluation.acknowledgedAtUtc).toLocaleString()} by {evaluation.acknowledgedBySubject}</p>
          {evaluation.capturedAtUtc && <p>Captured {new Date(evaluation.capturedAtUtc).toLocaleString()}</p>}
          <p>Toolhead at capture: X {evaluation.toolheadX ?? 'unknown'}, Y {evaluation.toolheadY ?? 'unknown'}, Z {evaluation.toolheadZ ?? 'unknown'}</p>
          {evaluation.groundTruth && <p>Independent label: {evaluation.groundTruth} by {evaluation.groundTruthBySubject}</p>}
        </div>
        {canLabel && imageReady && showImage && evaluation.state === 'Complete' && evaluation.hasImage && (
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-xs text-pf-text-secondary">Independent visual label:</span>
            <Button type="button" size="sm" variant="success" disabled={isLabeling} onClick={() => onLabel(evaluation.id, 'Clear')}>
              Bed clear
            </Button>
            <Button type="button" size="sm" variant="secondary" disabled={isLabeling} onClick={() => onLabel(evaluation.id, 'Occupied')}>
              Occupied
            </Button>
          </div>
        )}
        {canDelete && (
          <div className="flex justify-end">
            <Button type="button" size="sm" variant="danger" disabled={isDeleting} onClick={() => onDelete(evaluation)}>
              Delete sample/reference
            </Button>
          </div>
        )}
      </Card.Body>
    </Card>
  );
}

export function BedClearEvidencePanel({ printerId }: BedClearEvidencePanelProps) {
  const queryClient = useQueryClient();
  const { hasPermission, user } = useAuth();
  const titleId = useId();
  const [deleting, setDeleting] = useState<BedClearEvaluationDto | null>(null);
  const canLabel = hasPermission('queue', 'acknowledge-bed-clear');
  const canDelete = hasPermission('queue', 'write');
  const canRead = hasPermission('queue', 'read');

  const evaluationsQuery = useQuery({
    queryKey: evaluationsKey(printerId),
    queryFn: () => cameraService.getBedClearEvaluations(printerId),
    enabled: !!printerId && canRead,
    staleTime: 10_000,
    refetchInterval: (query) => query.state.data?.some(evaluation =>
      evaluation.state === 'Pending' || evaluation.state === 'Processing') ? 2_000 : false,
  });
  const metricsQuery = useQuery({
    queryKey: metricsKey(printerId),
    queryFn: () => cameraService.getBedClearDetectorMetrics(printerId),
    enabled: !!printerId && canRead,
    staleTime: 10_000,
  });

  const labelMutation = useMutation({
    mutationFn: ({ evaluationId, groundTruth }: { evaluationId: string; groundTruth: BedClearGroundTruth }) =>
      cameraService.labelBedClearEvaluation(evaluationId, groundTruth),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: evaluationsKey(printerId) }),
        queryClient.invalidateQueries({ queryKey: metricsKey(printerId) }),
      ]);
      toast.success('Independent bed-clear label saved.');
    },
    onError: (error) => toast.error(`Failed to label sample: ${error instanceof Error ? error.message : 'Unknown error'}`),
  });

  const captureMutation = useMutation({
    mutationFn: () => cameraService.captureBedClearEvaluation(printerId),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: evaluationsKey(printerId) });
      toast.success('Shadow sample requested. Readiness and dispatch are unchanged.');
    },
    onError: (error) => toast.error(`Failed to request sample: ${error instanceof Error ? error.message : 'Unknown error'}`),
  });

  const deleteMutation = useMutation({
    mutationFn: (evaluation: BedClearEvaluationDto) => cameraService.deleteBedClearEvaluation(evaluation.id),
    onSuccess: async () => {
      setDeleting(null);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: evaluationsKey(printerId) }),
        queryClient.invalidateQueries({ queryKey: metricsKey(printerId) }),
      ]);
      toast.success('Bed-clear sample/reference deleted.');
    },
    onError: (error) => toast.error(`Failed to delete sample/reference: ${error instanceof Error ? error.message : 'Unknown error'}`),
  });

  const evaluations = evaluationsQuery.data ?? [];
  if (!canRead) return <p className="text-sm text-pf-text-secondary">Queue read permission is required to review camera evidence.</p>;

  return (
    <section className="space-y-3 rounded-md border border-pf-border p-3" aria-labelledby={titleId}>
      <div>
        <h3 id={titleId} className="font-medium text-pf-text-primary">Camera bed-clear detector (shadow mode)</h3>
        <p className="text-xs text-pf-text-secondary">
          Samples are captured at acknowledgement without moving the printer. Detector output never changes dispatch.
          A different operator must label each image; only usable, independently labeled Clear samples become references.
        </p>
        <p className="mt-1 text-xs text-pf-text-secondary">
          Position the toolhead and bed consistently yourself. Use shadow capture for occupied-bed evaluation, never a Ready acknowledgement.
        </p>
        {canLabel && (
          <Button type="button" className="mt-2" size="sm" variant="secondary" disabled={captureMutation.isPending} onClick={() => captureMutation.mutate()}>
            Capture shadow sample (no dispatch)
          </Button>
        )}
      </div>

      {metricsQuery.isError ? (
        <Alert variant="error">Failed to load bed-clear detector metrics.</Alert>
      ) : metricsQuery.data ? (
        <div className="rounded border border-[var(--pf-border)] p-2 text-sm">
          <p>
            Agreement {formatRate(metricsQuery.data.agreementRate)} from {metricsQuery.data.independentlyLabeledCount} independent labels.
          </p>
          <p>
            False-clear rate {formatRate(metricsQuery.data.falseClearRate)} from {metricsQuery.data.independentlyLabeledOccupiedCount} independently labeled occupied samples.
          </p>
          {metricsQuery.data.falseClearRate === null && (
            <p className="text-xs text-pf-warning">Unavailable until at least one independently labeled occupied sample exists.</p>
          )}
        </div>
      ) : metricsQuery.isLoading ? (
        <div className="flex items-center gap-2 text-sm" role="status"><Spinner size="sm" /> Loading metrics…</div>
      ) : null}

      {evaluationsQuery.isError ? (
        <Alert variant="error">Failed to load bed-clear detector samples.</Alert>
      ) : evaluationsQuery.isLoading ? (
        <div className="flex items-center gap-2 text-sm" role="status"><Spinner size="sm" /> Loading samples…</div>
      ) : evaluations.length === 0 ? (
        <p className="text-sm text-pf-text-secondary">No acknowledgement samples are available yet.</p>
      ) : (
        <div className="space-y-2">
          {evaluations.map((evaluation) => (
            <EvaluationCard
              key={evaluation.id}
              evaluation={evaluation}
              canLabel={canLabel && !!user && user.id !== evaluation.acknowledgedBySubject}
              canDelete={canDelete}
              onLabel={(evaluationId, groundTruth) => labelMutation.mutate({ evaluationId, groundTruth })}
              onDelete={setDeleting}
              isLabeling={labelMutation.isPending}
              isDeleting={deleteMutation.isPending}
            />
          ))}
        </div>
      )}

      <ConfirmationModal
        isOpen={deleting !== null}
        title="Delete bed-clear evidence"
        message={deleting?.isActiveReference
          ? 'Delete this image and remove it from the active reference library? Future comparisons may return Unknown.'
          : 'Delete this evaluation sample and its image?'}
        confirmButtonText="Delete"
        isDangerous
        isConfirming={deleteMutation.isPending}
        onConfirm={() => deleting && deleteMutation.mutate(deleting)}
        onCancel={() => setDeleting(null)}
      />
    </section>
  );
}
