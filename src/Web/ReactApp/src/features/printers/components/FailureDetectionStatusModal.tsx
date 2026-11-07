import { useMemo, useState } from 'react';
import clsx from 'clsx';
import { Modal } from '@/common/components/modals/Modal';
import { Badge, Button } from '@/common/components/ui';
import { ExternalLinkIcon, ShieldIcon } from '@/common/components/icons/MdiIcons';
import { useFailureDetectionHistory } from '@/common/hooks/useApi';
import type { FailureDetectionEvent, FailureDetectionPrinterStatusDto } from '@/types/api';
import {
  getFailureDetectionAttentionContent,
  getFailureDetectionDetail,
  getFailureDetectionDisplayState,
  getFailureDetectionIconColorClass,
  getFailureDetectionSourceLabel,
  getFailureDetectionStateLabel,
  getFailureDetectionStateVariant,
} from '@/features/printers/utils/failureDetectionStatus';
import {
  getFailureDetectionIncidentContext,
  mergeFailureDetectionIncidents,
} from '@/features/printers/utils/failure-detection-incidents';
import { PrintSessionTimeline } from '@/features/printers/components/PrintSessionTimeline';
import { ConfidenceGauge } from '@/features/printers/components/ConfidenceGauge';

interface FailureDetectionStatusModalProps {
  isOpen: boolean;
  onClose: () => void;
  enabled: boolean;
  status?: FailureDetectionPrinterStatusDto;
  printerId?: string;
  printerName?: string;
  recentEvents?: FailureDetectionEvent[];
}

function formatFailureDetectionDateTime(value?: string): string | null {
  if (!value) {
    return null;
  }

  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) {
    return value;
  }

  return parsed.toLocaleString([], {
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  });
}

function getFailureDetectionOutcomeLabel(status?: FailureDetectionPrinterStatusDto): string | null {
  if (!status) {
    return null;
  }

  switch (status.lastOutcome) {
    case 'failure':
      return status.lastConfidence != null
        ? `Failure detected (${Math.round(status.lastConfidence * 100)}% confidence)`
        : 'Failure detected';
    case 'healthy':
      return status.lastAnalyzedAt
        ? 'No failure detected'
        : 'No recorded analysis observation';
    case 'error':
      return 'Monitoring runtime error';
    case 'none':
      return 'No scans yet';
    default:
      return status.lastOutcome;
  }
}

function getFailureDetectionErrorNextStep(reason: string): string | null {
  const normalizedReason = reason.toLowerCase();

  if (
    normalizedReason.includes('snapshot url request')
    || normalizedReason.includes('reachable from the detection provider')
    || normalizedReason.includes('private to the printer lan')
    || normalizedReason.includes('printer lan')
  ) {
    return 'Make the linked camera snapshot feed reachable from the configured detection provider, or switch to a camera feed that PrintFarmer can fetch locally.';
  }

  if (
    normalizedReason.includes('snapshot fetch timeout')
    || normalizedReason.includes('could not download the camera snapshot')
  ) {
    return 'Open the latest snapshot and confirm the camera responds from PrintFarmer before relying on failure alerts.';
  }

  if (
    normalizedReason.includes('analysis timed out')
    || normalizedReason.includes('timed out while analyzing')
    || normalizedReason.includes('request timeout')
  ) {
    return 'Check the detection provider and camera reachability, then retry once the provider is responding normally.';
  }

  return null;
}

function getFailureDetectionNextStep(
  status: FailureDetectionPrinterStatusDto | undefined,
  enabled: boolean
): string {
  const displayState = getFailureDetectionDisplayState(status);

  if (!status) {
    return enabled
      ? 'Give the runtime a moment to report whether this print is actively being watched.'
      : 'Enable failure detection before expecting live monitoring on this printer.';
  }

  switch (displayState ?? status.state) {
    case 'reservation-held':
      return getFailureDetectionAttentionContent(status)?.action ?? status.reason;
    case 'unknown':
      return 'Restore printer connectivity and verify its physical state before assuming the print has ended.';
    case 'monitoring':
      return 'No operator action is needed right now. Failure detection is actively watching the current print.';
    case 'idle':
      return 'No action is needed until this printer starts a new print. Monitoring will resume automatically when a job is active.';
    case 'misconfigured':
      return 'Add and enable a usable linked camera snapshot feed so failure detection can inspect frames from this printer.';
    case 'error':
      return getFailureDetectionErrorNextStep(status.reason)
        ?? 'Check the failure-detection service connection and camera reachability before relying on failure alerts.';
    case 'disabled':
      return enabled
        ? 'Monitoring is standing by. Start a print or verify this printer is eligible for the runtime to begin scanning.'
        : 'Turn on failure detection for this printer if you want the runtime to watch active jobs.';
    default:
      return enabled
        ? 'Wait for the monitoring runtime to confirm current coverage for this printer.'
        : 'Enable failure detection before expecting live monitoring detail for this printer.';
  }
}

function DetailTile({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-lg border border-pf-border bg-pf-bg-0 px-4 py-3">
      <div className="text-xs font-semibold uppercase tracking-[0.16em] text-pf-text-secondary">
        {label}
      </div>
      <div className="mt-1 text-sm text-pf-text-primary">{value}</div>
    </div>
  );
}

function getProviderOutcomeLabel(outcome: string, confidence?: number): string {
  switch (outcome) {
    case 'failure':
      return confidence == null
        ? 'Failure detected'
        : `Failure detected (${Math.round(confidence * 100)}% confidence)`;
    case 'healthy':
      return 'No failure detected';
    case 'error':
      return 'Analysis error';
    case 'none':
      return 'No scans yet';
    default:
      return outcome;
  }
}

export function FailureDetectionStatusModal({
  isOpen,
  onClose,
  enabled,
  status,
  printerId,
  printerName,
  recentEvents = [],
}: FailureDetectionStatusModalProps) {
  const [selectedTimelineJobId, setSelectedTimelineJobId] = useState<string | null>(null);
  const resolvedPrinterId = status?.printerId ?? printerId;
  const resolvedPrinterName = status?.printerName ?? printerName ?? 'This printer';
  const {
    data: persistedIncidents = [],
    isLoading: isHistoryLoading,
    isError: hasHistoryError,
  } = useFailureDetectionHistory(resolvedPrinterId, 5, {
    enabled: isOpen && !!resolvedPrinterId,
  });
  const displayState = getFailureDetectionDisplayState(status) ?? status?.state;
  const statusLabel = getFailureDetectionStateLabel(displayState, enabled);
  const statusVariant = getFailureDetectionStateVariant(displayState, enabled);
  const detail = getFailureDetectionDetail(status, enabled);
  const reason = status?.reason ?? (enabled
    ? 'The monitoring runtime has not reported printer-specific detail yet.'
    : 'Failure detection is currently disabled for this printer.');
  const sourceLabel = getFailureDetectionSourceLabel(status?.detectionSource)
    ?? (status?.detectionSource === 'none' ? 'Not assigned' : null);
  const detectionTarget = status?.detectionTarget?.trim() || null;
  const lastScan = formatFailureDetectionDateTime(status?.lastAnalyzedAt);
  const lastFailure = formatFailureDetectionDateTime(status?.lastFailureDetectedAt);
  const lastOutcome = getFailureDetectionOutcomeLabel(status);
  const nextStep = getFailureDetectionNextStep(status, enabled);
  // Same derivation as FailureDetectionMonitoringBadge, without the raw-state fallback,
  // so a non-printing error shows the same shield tone in the badge and the modal.
  // The status badge below intentionally keeps the `?? status?.state` fallback: the
  // shield mirrors the summary the operator clicked ("Checking"), while the modal
  // body reports the raw backend state ("Monitor error") for diagnosis.
  const iconColorClass = getFailureDetectionIconColorClass(getFailureDetectionDisplayState(status), enabled);
  const showReason = reason.trim() !== detail.trim();
  const needsAttention = getFailureDetectionAttentionContent(status) !== null;
  const recentIncidents = useMemo(
    () => mergeFailureDetectionIncidents(recentEvents, persistedIncidents).slice(0, 5),
    [persistedIncidents, recentEvents]
  );
  const timelineSessions = useMemo(() => {
    const sessionsByJobId = new Map<string, {
      jobId: string;
      label: string;
      latestDetectedAt: string;
      incidents: FailureDetectionEvent[];
    }>();

    for (const incident of recentIncidents) {
      if (!incident.jobId) {
        continue;
      }

      const nextLabel = incident.jobName?.trim() || incident.fileName?.trim() || 'Tracked print session';
      const existingSession = sessionsByJobId.get(incident.jobId);

      if (existingSession) {
        existingSession.incidents.push(incident);

        if (new Date(incident.detectedAt).getTime() > new Date(existingSession.latestDetectedAt).getTime()) {
          existingSession.latestDetectedAt = incident.detectedAt;
          existingSession.label = nextLabel;
        }

        continue;
      }

      sessionsByJobId.set(incident.jobId, {
        jobId: incident.jobId,
        label: nextLabel,
        latestDetectedAt: incident.detectedAt,
        incidents: [incident],
      });
    }

    return Array.from(sessionsByJobId.values())
      .sort((leftSession, rightSession) => (
        new Date(rightSession.latestDetectedAt).getTime() - new Date(leftSession.latestDetectedAt).getTime()
      ))
      .map((timelineSession) => ({
        ...timelineSession,
        incidents: [...timelineSession.incidents].sort((leftIncident, rightIncident) => (
          new Date(leftIncident.detectedAt).getTime() - new Date(rightIncident.detectedAt).getTime()
        )),
      }));
  }, [recentIncidents]);
  const resolvedSelectedTimelineJobId = selectedTimelineJobId
    && timelineSessions.some((timelineSession) => timelineSession.jobId === selectedTimelineJobId)
    ? selectedTimelineJobId
    : (timelineSessions[0]?.jobId ?? null);
  const selectedTimelineSession = timelineSessions.find(
    (timelineSession) => timelineSession.jobId === resolvedSelectedTimelineJobId
  ) ?? timelineSessions[0];
  const shouldShowRecentIncidentSection = !!resolvedPrinterId || recentIncidents.length > 0;
  const hasUntiedTimelineIncidents = recentIncidents.some((incident) => !incident.jobId);

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title="Failure detection details"
      width="max-w-4xl"
      maxHeight="max-h-[85vh]"
      titleIcon={<ShieldIcon className={clsx('h-5 w-5', iconColorClass)} ariaLabel="Failure detection" />}
      closeAriaLabel={`Close failure detection details for ${resolvedPrinterName}`}
    >
      <div className="space-y-5">
        {/* Status header — always full width */}
        <div className="rounded-lg border border-pf-border bg-pf-bg-0 p-4">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div>
              <div className="text-xs font-semibold uppercase tracking-[0.16em] text-pf-text-secondary">
                Printer
              </div>
              <h3 className="mt-1 text-lg font-semibold text-pf-text-primary">
                {resolvedPrinterName}
              </h3>
            </div>
            <Badge variant={statusVariant} size="sm">
              {statusLabel}
            </Badge>
          </div>
          <p className="mt-3 text-sm leading-6 text-pf-text-primary">{detail}</p>
          <p className="mt-2 text-xs text-pf-text-secondary">
            Detection findings do not trigger automatic printer actions.
          </p>
          {status?.state === 'monitoring' && status.lastConfidence != null && (
            <div className="mt-4 flex items-end gap-4">
              <ConfidenceGauge value={1 - status.lastConfidence} size="lg" />
              <span className="pb-1 text-xs text-pf-text-secondary">
                Confidence
              </span>
            </div>
          )}
        </div>

        {/* Detail tiles — always full width */}
        <div className="grid gap-3 sm:grid-cols-2">
          <DetailTile
            label="Coverage source"
            value={sourceLabel ?? (enabled ? 'Runtime is still checking' : 'Disabled')}
          />
          <DetailTile
            label={status?.state === 'reservation-held' ? 'Reserved server' : 'Watching'}
            value={detectionTarget ?? 'Current camera target not reported'}
          />
          {lastScan && <DetailTile label="Last scan" value={lastScan} />}
          {lastOutcome && <DetailTile label="Last analysis outcome" value={lastOutcome} />}
          {lastFailure && <DetailTile label="Last failure detected" value={lastFailure} />}
        </div>

        {status?.providers && status.providers.length > 0 && (
          <section className="space-y-3">
            <h3 className="text-sm font-semibold uppercase tracking-[0.16em] text-pf-text-secondary">
              Provider results
            </h3>
            <div className="grid gap-3 sm:grid-cols-2">
              {status.providers.map((provider) => {
                const providerName = provider.provider === 'PrintGuard'
                  ? 'PrintGuard'
                  : provider.provider === 'Obico'
                    ? 'Obico'
                    : provider.provider;
                const providerState = getFailureDetectionStateLabel(provider.state, provider.state !== 'disabled');
                const analyzedAt = formatFailureDetectionDateTime(provider.lastAnalyzedAt);

                return (
                  <div
                    key={provider.provider}
                    className="rounded-lg border border-pf-border bg-pf-bg-0 px-4 py-3"
                    data-testid={`failure-detection-provider-${provider.provider.toLowerCase()}`}
                  >
                    <div className="flex flex-wrap items-center justify-between gap-2">
                      <h4 className="font-medium text-pf-text-primary">{providerName}</h4>
                      <Badge variant={getFailureDetectionStateVariant(provider.state)} size="sm">
                        {providerState}
                      </Badge>
                    </div>
                    <p className="mt-2 text-sm text-pf-text-primary">{provider.reason}</p>
                    <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-pf-text-secondary">
                      <span>
                        {provider.lastOutcome === 'healthy' && !analyzedAt
                          ? 'No recorded analysis observation'
                          : `Last analysis: ${getProviderOutcomeLabel(provider.lastOutcome, provider.lastConfidence)}`}
                      </span>
                      {analyzedAt && (
                        <span>{provider.analysisMode === 'paired-frame' ? 'Paired frame' : 'Unpaired analysis'}</span>
                      )}
                      {provider.state === 'unknown' && <span>Current coverage unconfirmed</span>}
                      {analyzedAt && <span>Last analyzed {analyzedAt}</span>}
                    </div>
                  </div>
                );
              })}
            </div>
          </section>
        )}

        {/* 2-column layout on large screens: context/action left, history right */}
        <div className="grid gap-5 lg:grid-cols-2">
          {/* Left column — context and operator guidance */}
          <div className="space-y-5">
            {showReason && (
              <section className="space-y-2">
                <h3 className="text-sm font-semibold uppercase tracking-[0.16em] text-pf-text-secondary">
                  Why this is showing
                </h3>
                <div className="rounded-lg border border-pf-border bg-pf-bg-0 px-4 py-3 text-sm leading-6 text-pf-text-primary">
                  {reason}
                </div>
              </section>
            )}

            <section className="space-y-2">
              <h3 className="text-sm font-semibold uppercase tracking-[0.16em] text-pf-text-secondary">
                Operator next step
              </h3>
              <div
                data-testid="failure-detection-next-step"
                className={clsx(
                  'rounded-lg border px-4 py-3 text-sm leading-6 text-pf-text-primary',
                  needsAttention ? 'border-pf-warning/25 bg-pf-warning/10' : 'border-pf-border bg-pf-bg-0'
                )}
              >
                {nextStep}
              </div>
            </section>

            {status?.snapshotUrl && (
              <div className="rounded-lg border border-pf-accent/20 bg-pf-accent-bg/40 px-4 py-3">
                <a
                  href={status.snapshotUrl}
                  target="_blank"
                  rel="noopener noreferrer"
                  className="inline-flex items-center gap-2 text-sm font-medium text-pf-accent hover:underline underline-offset-2"
                >
                  Open latest snapshot
                  <ExternalLinkIcon className="h-4 w-4" />
                </a>
              </div>
            )}
          </div>

          {/* Right column — incident history and timeline */}
          <div className="space-y-5">
            {shouldShowRecentIncidentSection && (
              <section className="space-y-2">
                <h3 className="text-sm font-semibold uppercase tracking-[0.16em] text-pf-text-secondary">
                  Recent incidents
                </h3>
                {isHistoryLoading && recentIncidents.length === 0 && (
                  <div className="rounded-lg border border-pf-border bg-pf-bg-0 px-4 py-3 text-sm text-pf-text-secondary">
                    Loading recent incident history…
                  </div>
                )}

                {!isHistoryLoading && hasHistoryError && recentIncidents.length === 0 && (
                  <div className="rounded-lg border border-pf-warning/25 bg-pf-warning/10 px-4 py-3 text-sm text-pf-text-primary">
                    Recent incident history is unavailable right now. Live monitoring details are still shown above.
                  </div>
                )}

                {!isHistoryLoading && !hasHistoryError && recentIncidents.length === 0 && (
                  <div className="rounded-lg border border-pf-border bg-pf-bg-0 px-4 py-3 text-sm text-pf-text-secondary">
                    No persisted incidents have been recorded for this printer yet.
                  </div>
                )}

                {recentIncidents.length > 0 && (
                  <div className="space-y-2">
                    {recentIncidents.map((event, index) => {
                      const detectedAt = formatFailureDetectionDateTime(event.detectedAt) ?? event.detectedAt;
                      const confidencePercent = Math.round(event.confidence * 100);
                      const incidentContext = getFailureDetectionIncidentContext(event);

                      return (
                        <div
                          key={event.id ?? `${event.detectedAt}-${event.confidence}-${index}`}
                          className="rounded-lg border border-pf-border bg-pf-bg-0 px-4 py-3"
                        >
                          <div className="flex flex-wrap items-center gap-2">
                            <Badge variant={event.autoPaused ? 'error' : 'warning'} size="sm">
                              {event.provider} · {confidencePercent}% confidence
                            </Badge>
                            <span className="text-sm text-pf-text-primary">{detectedAt}</span>
                            <span className="text-xs font-semibold uppercase tracking-[0.16em] text-pf-text-secondary">
                              {event.autoPaused ? 'Auto-paused' : 'Review required'}
                            </span>
                          </div>

                          {incidentContext.length > 0 && (
                            <div className="mt-3 flex flex-wrap gap-2 text-sm text-pf-text-primary">
                              {incidentContext.map((context) => (
                                <span
                                  key={`${event.id ?? event.detectedAt}-${context.label}`}
                                  className="inline-flex items-center gap-1 rounded-xs bg-pf-bg-1 px-2.5 py-1"
                                >
                                  <span className="text-xs font-semibold uppercase tracking-[0.12em] text-pf-text-secondary">
                                    {context.label}
                                  </span>
                                  <span>{context.value}</span>
                                </span>
                              ))}
                            </div>
                          )}

                          {event.snapshotUrl && (
                            <a
                              href={event.snapshotUrl}
                              target="_blank"
                              rel="noopener noreferrer"
                              className="mt-3 inline-flex items-center gap-2 text-sm font-medium text-pf-accent hover:underline underline-offset-2"
                            >
                              Open incident snapshot
                              <ExternalLinkIcon className="h-4 w-4" />
                            </a>
                          )}
                        </div>
                      );
                    })}
                  </div>
                )}
              </section>
            )}

            <section className="space-y-3">
              <div className="space-y-2">
                <h3 className="text-sm font-semibold uppercase tracking-[0.16em] text-pf-text-secondary">
                  Print session timeline
                </h3>
                <p className="text-sm leading-6 text-pf-text-secondary">
                  Use the selected session to see what happened around a failure: when the job queued,
                  when printing started, when failure detection fired, and what action followed.
                </p>
              </div>

              {timelineSessions.length > 1 && (
                <div
                  className="flex flex-wrap gap-2"
                  role="group"
                  aria-label="Choose a print session timeline"
                >
                  {timelineSessions.map((timelineSession) => {
                    const isSelected = timelineSession.jobId === selectedTimelineSession?.jobId;

                    return (
                      <Button
                        key={timelineSession.jobId}
                        type="button"
                        size="sm"
                        variant={isSelected ? 'secondary' : 'subtle'}
                        aria-pressed={isSelected}
                        className="justify-start text-left"
                        onClick={() => setSelectedTimelineJobId(timelineSession.jobId)}
                      >
                        <span className="flex flex-col items-start">
                          <span className="font-medium text-pf-text-primary">{timelineSession.label}</span>
                          <span className="text-[11px] uppercase tracking-[0.14em] text-pf-text-secondary">
                            {formatFailureDetectionDateTime(timelineSession.latestDetectedAt) ?? timelineSession.latestDetectedAt}
                          </span>
                        </span>
                      </Button>
                    );
                  })}
                </div>
              )}

              {selectedTimelineSession ? (
                <PrintSessionTimeline
                  jobId={selectedTimelineSession.jobId}
                  jobLabel={selectedTimelineSession.label}
                  incidents={selectedTimelineSession.incidents}
                />
              ) : (
                <div className="rounded-lg border border-pf-border bg-pf-bg-0 px-4 py-3 text-sm text-pf-text-secondary">
                  Session timeline will appear once an incident can be tied to a tracked PrintFarmer job.
                </div>
              )}

              {hasUntiedTimelineIncidents && (
                <div className="rounded-lg border border-pf-border bg-pf-bg-0 px-4 py-3 text-xs leading-5 text-pf-text-secondary">
                  Some incidents are still shown above without session timelines because they do not carry a
                  tracked PrintFarmer job ID.
                </div>
              )}
            </section>
          </div>
        </div>
      </div>
    </Modal>
  );
}
