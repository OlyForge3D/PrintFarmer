import { GcodeHarvestOperation } from '@/types/api';

export interface ErrorInfo {
  title: string;
  message: string;
  suggestion?: string;
  iconType: 'connection' | 'auth' | 'filesystem' | 'validation' | 'unknown';
  canRetry: boolean;
  phase?: string;
  failedResource?: string;
}

/**
 * Get comprehensive error information for display
 */
export function getHarvestErrorInfo(operation: GcodeHarvestOperation): ErrorInfo | null {
  if (!operation.error && !operation.errorType) {
    return null;
  }

  const errorType = operation.errorType || 'UnknownError';
  const phase = operation.errorPhase;
  const failedResource = operation.failedResource;
  const canRetry = operation.isRetryable ?? false;

  const errorInfo = getErrorDetails(errorType);

  return {
    ...errorInfo,
    message: operation.error || 'An unknown error occurred',
    canRetry,
    phase,
    failedResource,
  };
}

function getErrorDetails(errorType: string): Omit<ErrorInfo, 'message' | 'canRetry' | 'phase' | 'failedResource'> {
  switch (errorType) {
    case 'ConnectionError':
      return {
        title: 'Connection Failed',
        suggestion: 'Verify printer is online and URL is correct. Check your network connection.',
        iconType: 'connection',
      };
    case 'AuthenticationError':
      return {
        title: 'Authentication Failed',
        suggestion: 'Check your API key is valid and has the required permissions.',
        iconType: 'auth',
      };
    case 'FileSystemError':
      return {
        title: 'File System Error',
        suggestion: 'Ensure the printer has the requested files and folders accessible.',
        iconType: 'filesystem',
      };
    case 'ValidationError':
      return {
        title: 'Validation Failed',
        suggestion: 'Check the harvest operation settings and try again.',
        iconType: 'validation',
      };
    default:
      return {
        title: 'Harvest Failed',
        suggestion: undefined,
        iconType: 'unknown',
      };
  }
}

/**
 * Get formatted phase name
 */
export function getPhaseDisplay(phase?: string): string {
  if (!phase) return '';
  
  switch (phase) {
    case 'Discovery':
      return 'during file discovery';
    case 'Download':
      return 'during file download';
    case 'Processing':
      return 'during file processing';
    case 'Completion':
      return 'during completion';
    default:
      return '';
  }
}
