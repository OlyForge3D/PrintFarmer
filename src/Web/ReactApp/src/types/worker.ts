/**
 * Type definitions for Worker entities and related data structures
 */

/**
 * Worker response from the API
 * Represents a registered slicer worker node
 */
export interface WorkerResponse {
  id: string;
  serviceId: string;
  name: string;
  endpointUrl: string;
  capabilities: string[];
  status: string; // "Online" | "Offline" | "Busy" | "Error" | "Draining"
  freeSlots: number;
  totalSlots: number;
  activeJobs: number;
  completedJobs: number;
  failedJobs: number;
  averageProcessingTimeSeconds?: number;
  lastHeartbeat: string; // ISO 8601 datetime
  registeredAt: string; // ISO 8601 datetime
  onlineAt?: string; // ISO 8601 datetime
  offlineAt?: string; // ISO 8601 datetime
  version: string;
  isDisabled: boolean;
  disabledReason?: string;
}

/**
 * Worker status enumeration
 */
export enum WorkerStatus {
  Online = 'Online',
  Offline = 'Offline',
  Busy = 'Busy',
  Error = 'Error',
  Draining = 'Draining',
}

/**
 * Helper to determine if worker is available for new jobs
 */
export function isWorkerAvailable(worker: WorkerResponse): boolean {
  return (
    !worker.isDisabled &&
    worker.status === WorkerStatus.Online &&
    worker.freeSlots > 0
  );
}

/**
 * Helper to check if worker has all required capabilities
 */
export function hasRequiredCapabilities(
  worker: WorkerResponse,
  requiredCapabilities: string[]
): boolean {
  if (requiredCapabilities.length === 0) {
    return true;
  }

  return requiredCapabilities.every(requiredCap =>
    worker.capabilities.some(cap => cap.toLowerCase() === requiredCap.toLowerCase())
  );
}

/**
 * Helper to format worker capacity display
 */
export function formatWorkerCapacity(worker: WorkerResponse): string {
  return `${worker.freeSlots}/${worker.totalSlots} slots`;
}

/**
 * Helper to format worker utilization percentage
 */
export function calculateWorkerUtilization(worker: WorkerResponse): number {
  if (worker.totalSlots === 0) return 0;
  const usedSlots = worker.totalSlots - worker.freeSlots;
  return Math.round((usedSlots / worker.totalSlots) * 100);
}

/**
 * Helper to get worker status badge color
 */
export function getWorkerStatusColor(status: string): string {
  switch (status) {
    case WorkerStatus.Online:
      return 'bg-pf-success';
    case WorkerStatus.Busy:
      return 'bg-pf-warning';
    case WorkerStatus.Offline:
      return 'bg-pf-disabled';
    case WorkerStatus.Error:
      return 'bg-pf-error';
    case WorkerStatus.Draining:
      return 'bg-pf-warning';
    default:
      return 'bg-pf-disabled';
  }
}
