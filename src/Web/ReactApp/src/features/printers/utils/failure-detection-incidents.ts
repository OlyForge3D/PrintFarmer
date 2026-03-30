import type { FailureDetectionEvent } from '@/types/api';

export function getFailureDetectionIncidentKey(event: FailureDetectionEvent): string {
  return [
    event.id ?? '',
    event.printerId,
    event.detectedAt,
    event.confidence,
    event.autoPaused ? 'paused' : 'review',
    event.snapshotUrl ?? '',
    event.jobId ?? '',
    event.jobName ?? '',
    event.fileName ?? '',
  ].join('::');
}

export function mergeFailureDetectionIncidents(
  ...incidentGroups: FailureDetectionEvent[][]
): FailureDetectionEvent[] {
  const incidentsByKey = new Map<string, FailureDetectionEvent>();

  for (const incidentGroup of incidentGroups) {
    for (const incident of incidentGroup) {
      incidentsByKey.set(getFailureDetectionIncidentKey(incident), incident);
    }
  }

  return Array.from(incidentsByKey.values()).sort((leftIncident, rightIncident) => (
    new Date(rightIncident.detectedAt).getTime() - new Date(leftIncident.detectedAt).getTime()
  ));
}

export function getFailureDetectionIncidentContext(event: FailureDetectionEvent): Array<{
  label: string;
  value: string;
}> {
  const context: Array<{ label: string; value: string }> = [];

  if (event.jobName?.trim()) {
    context.push({ label: 'Job', value: event.jobName.trim() });
  }

  if (event.fileName?.trim()) {
    context.push({ label: 'File', value: event.fileName.trim() });
  }

  return context;
}
