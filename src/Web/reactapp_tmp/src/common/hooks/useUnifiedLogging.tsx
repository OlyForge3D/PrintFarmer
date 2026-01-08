// This file contains unified logging hooks that need to handle various data types
// Prefer `unknown` for external inputs and narrow before reading properties.
// No eslint disable needed; prefer narrow casts where necessary

import { useEffect, useCallback, useMemo } from 'react';
import { unifiedLogger, loggerExtensions, type LogEntry } from '@/services/unifiedLogging';

export interface UseUnifiedLoggingOptions {
  component?: string;
  userId?: string;
  logLifecycle?: boolean;
}

export function useUnifiedLogging(options: UseUnifiedLoggingOptions = {}) {
  const { component = 'UnknownComponent', userId, logLifecycle = true } = options;

  // Set user ID if provided
  useEffect(() => {
    if (userId) {
      unifiedLogger.setUserId(userId);
    }
  }, [userId]);

  // Log component lifecycle
  useEffect(() => {
    if (logLifecycle) {
      unifiedLogger.logComponentLifecycle(component, 'mount');
      
      return () => {
        unifiedLogger.logComponentLifecycle(component, 'unmount');
      };
    }
  }, [component, logLifecycle]);

  // Create component-specific logger methods
  const logger = {
    debug: useCallback((message: string, context?: unknown) => {
      unifiedLogger.debug(message, context, component);
    }, [component]),

    info: useCallback((message: string, context?: unknown) => {
      unifiedLogger.info(message, context, component);
    }, [component]),

    warn: useCallback((message: string, context?: unknown) => {
      unifiedLogger.warn(message, context, component);
    }, [component]),

    error: useCallback((message: string, context?: unknown) => {
      unifiedLogger.error(message, context, component);
    }, [component]),

    logUpdate: useCallback((details?: unknown) => {
      if (logLifecycle) {
        unifiedLogger.logComponentLifecycle(component, 'update', details);
      }
    }, [component, logLifecycle]),

    logUserAction: useCallback((action: string, details?: unknown) => {
      unifiedLogger.logUserAction(action, component, details);
    }, [component]),

    logApiRequest: useCallback((method: string, url: string, statusCode: number, duration: number, details?: unknown) => {
  unifiedLogger.logApiRequest(method, url, statusCode, duration, details as unknown as Record<string, unknown>);
    }, []),

    logSignalREvent: useCallback((event: string, connectionState: string, details?: unknown) => {
  unifiedLogger.logSignalREvent(event, connectionState, details as unknown as Record<string, unknown>);
    }, []),

    logPrinterOperation: loggerExtensions.logPrinterOperation,
    logFileOperation: loggerExtensions.logFileOperation,
    logNavigation: loggerExtensions.logNavigation,
    logFormSubmission: loggerExtensions.logFormSubmission,
  };

  // Debug helpers - stabilized with useMemo to prevent infinite re-renders
  // Remove dependency on logger to break circular dependency
  const debugHelpers = useMemo(() => ({
    getStoredLogs: (): LogEntry[] => {
      return unifiedLogger.getStoredLogs();
    },

    clearStoredLogs: () => {
      unifiedLogger.clearStoredLogs();
    },

    exportLogs: (): string => {
      return unifiedLogger.exportLogs();
    },

    downloadLogs: () => {
      const logs = unifiedLogger.exportLogs();
      const blob = new Blob([logs], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `printfarmer-logs-${new Date().toISOString().slice(0, 19)}.json`;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      URL.revokeObjectURL(url);
      // Log directly to avoid circular dependency
      unifiedLogger.info('Logs downloaded', { filename: a.download }, component);
    },
  }), [component]);

  return { logger, debugHelpers };
}

// Hook specifically for API client logging
export function useApiLogging() {
  const logApiCall = useCallback(<T,>(
    promise: Promise<T>,
    method: string,
    url: string,
    requestDetails?: unknown
  ): Promise<T> => {
    return (async (): Promise<T> => {
      const startTime = Date.now();
      
      try {
        const result = await promise;
        const duration = Date.now() - startTime;
        
        unifiedLogger.logApiRequest(method, url, 200, duration, {
          ...(requestDetails as unknown as Record<string, unknown>),
          success: true,
        });
        
        return result;
      } catch (error: unknown) {
        const duration = Date.now() - startTime;
        // Narrow error to an object with optional response/status and message
        const err = error as { response?: { status?: number }; message?: string; name?: string };
        const statusCode = err.response?.status || 500;

        unifiedLogger.logApiRequest(method, url, statusCode, duration, {
          ...(requestDetails as unknown as Record<string, unknown>),
          success: false,
          error: err.message ?? String(error),
          errorType: err.name ?? 'Error',
  } as Record<string, unknown>);

        throw error;
      }
    })();
  }, []);

  return { logApiCall };
}

// Hook for SignalR connection logging
export function useSignalRLogging() {
  const logConnectionEvent = useCallback((event: string, connectionState: string, details?: unknown) => {
  unifiedLogger.logSignalREvent(event, connectionState, details as unknown as Record<string, unknown>);
  }, []);

  const logMessageReceived = useCallback((messageType: string, payload?: unknown) => {
  unifiedLogger.info(`SignalR message received: ${messageType}`, payload as unknown as Record<string, unknown>, 'SignalR');
  }, []);

  const logMessageSent = useCallback((messageType: string, payload?: unknown) => {
  unifiedLogger.info(`SignalR message sent: ${messageType}`, payload as unknown as Record<string, unknown>, 'SignalR');
  }, []);

  return { logConnectionEvent, logMessageReceived, logMessageSent };
}

// Hook for form logging
export function useFormLogging(formName: string) {
  const logger = useCallback((success: boolean, validationErrors?: unknown, details?: unknown) => {
  loggerExtensions.logFormSubmission(formName, success, validationErrors as unknown as Record<string, unknown>, details as unknown as Record<string, unknown>);
  }, [formName]);

  const logFieldChange = useCallback((fieldName: string, newValue: unknown, oldValue?: unknown) => {
    unifiedLogger.debug(`Form field changed: ${fieldName}`, {
      formName,
      fieldName,
      newValue,
      oldValue,
    }, 'FormField');
  }, [formName]);

  const logValidationError = useCallback((fieldName: string, error: string) => {
    unifiedLogger.warn(`Form validation error: ${fieldName}`, {
      formName,
      fieldName,
      error,
    }, 'FormValidation');
  }, [formName]);

  return { 
    logSubmission: logger,
    logFieldChange,
    logValidationError,
  };
}