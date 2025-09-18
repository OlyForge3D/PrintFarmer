// This file contains unified logging hooks that need to handle various data types
// TypeScript 'any' is acceptable here for logging flexible data structures
/* eslint-disable @typescript-eslint/no-explicit-any */

import { useEffect, useCallback, useMemo } from 'react';
import { unifiedLogger, loggerExtensions, type LogEntry } from '../services/unifiedLogging';

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
    debug: useCallback((message: string, context?: any) => {
      unifiedLogger.debug(message, context, component);
    }, [component]),

    info: useCallback((message: string, context?: any) => {
      unifiedLogger.info(message, context, component);
    }, [component]),

    warn: useCallback((message: string, context?: any) => {
      unifiedLogger.warn(message, context, component);
    }, [component]),

    error: useCallback((message: string, context?: any) => {
      unifiedLogger.error(message, context, component);
    }, [component]),

    logUpdate: useCallback((details?: any) => {
      if (logLifecycle) {
        unifiedLogger.logComponentLifecycle(component, 'update', details);
      }
    }, [component, logLifecycle]),

    logUserAction: useCallback((action: string, details?: any) => {
      unifiedLogger.logUserAction(action, component, details);
    }, [component]),

    logApiRequest: useCallback((method: string, url: string, statusCode: number, duration: number, details?: any) => {
      unifiedLogger.logApiRequest(method, url, statusCode, duration, details);
    }, []),

    logSignalREvent: useCallback((event: string, connectionState: string, details?: any) => {
      unifiedLogger.logSignalREvent(event, connectionState, details);
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
    requestDetails?: any
  ): Promise<T> => {
    return (async (): Promise<T> => {
      const startTime = Date.now();
      
      try {
        const result = await promise;
        const duration = Date.now() - startTime;
        
        unifiedLogger.logApiRequest(method, url, 200, duration, {
          ...requestDetails,
          success: true,
        });
        
        return result;
      } catch (error: any) {
        const duration = Date.now() - startTime;
        const statusCode = error.response?.status || 500;
        
        unifiedLogger.logApiRequest(method, url, statusCode, duration, {
          ...requestDetails,
          success: false,
          error: error.message,
          errorType: error.name,
        });
        
        throw error;
      }
    })();
  }, []);

  return { logApiCall };
}

// Hook for SignalR connection logging
export function useSignalRLogging() {
  const logConnectionEvent = useCallback((event: string, connectionState: string, details?: any) => {
    unifiedLogger.logSignalREvent(event, connectionState, details);
  }, []);

  const logMessageReceived = useCallback((messageType: string, payload?: any) => {
    unifiedLogger.info(`SignalR message received: ${messageType}`, payload, 'SignalR');
  }, []);

  const logMessageSent = useCallback((messageType: string, payload?: any) => {
    unifiedLogger.info(`SignalR message sent: ${messageType}`, payload, 'SignalR');
  }, []);

  return { logConnectionEvent, logMessageReceived, logMessageSent };
}

// Hook for form logging
export function useFormLogging(formName: string) {
  const logger = useCallback((success: boolean, validationErrors?: any, details?: any) => {
    loggerExtensions.logFormSubmission(formName, success, validationErrors, details);
  }, [formName]);

  const logFieldChange = useCallback((fieldName: string, newValue: any, oldValue?: any) => {
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