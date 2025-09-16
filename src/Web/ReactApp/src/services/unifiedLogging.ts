// This file contains unified logging code that needs to handle various data types
// TypeScript 'any' is acceptable here for logging flexible data structures
/* eslint-disable @typescript-eslint/no-explicit-any */
/* eslint-disable @typescript-eslint/no-unsafe-function-type */

import { trace } from '@opentelemetry/api';

export interface LogEntry {
  level: 'debug' | 'info' | 'warn' | 'error';
  message: string;
  context?: any;
  timestamp: Date;
  component?: string;
  userId?: string;
  sessionId?: string;
}

export interface IUnifiedLoggingService {
  debug(message: string, context?: any, component?: string): void;
  info(message: string, context?: any, component?: string): void;
  warn(message: string, context?: any, component?: string): void;
  error(message: string, context?: any, component?: string): void;
  
  // Context-aware logging for specific scenarios
  logApiRequest(method: string, url: string, statusCode: number, duration: number, details?: any): void;
  logUserAction(action: string, component: string, details?: any): void;
  logSignalREvent(event: string, connectionState: string, details?: any): void;
  logComponentLifecycle(component: string, phase: 'mount' | 'unmount' | 'update', details?: any): void;
}

class UnifiedLoggingService implements IUnifiedLoggingService {
  private tracer = trace.getTracer('PrintFarmer.Frontend.Logging');
  private sessionId = this.generateSessionId();
  private userId?: string;

  // Store original console methods
  private originalConsole = {
    log: console.log.bind(console),
    info: console.info.bind(console),
    warn: console.warn.bind(console),
    error: console.error.bind(console),
    debug: console.debug.bind(console),
  };

  constructor() {
    this.initializeConsoleRedirection();
  }

  private generateSessionId(): string {
    return `frontend-session-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;
  }

  setUserId(userId: string): void {
    this.userId = userId;
  }

  private initializeConsoleRedirection(): void {
    // Redirect console methods to our unified logging
    console.log = this.createConsoleRedirect('info', this.originalConsole.log);
    console.info = this.createConsoleRedirect('info', this.originalConsole.info);
    console.warn = this.createConsoleRedirect('warn', this.originalConsole.warn);
    console.error = this.createConsoleRedirect('error', this.originalConsole.error);
    console.debug = this.createConsoleRedirect('debug', this.originalConsole.debug);
  }

  private createConsoleRedirect(level: LogEntry['level'], originalMethod: Function) {
    return (...args: any[]) => {
      // Call original console method for development debugging
      originalMethod(...args);
      
      // Extract message and context from arguments
      const message = args.map(arg => 
        typeof arg === 'string' ? arg : 
        typeof arg === 'object' ? JSON.stringify(arg, null, 2) : 
        String(arg)
      ).join(' ');

      // Create telemetry span for the log
      this.logWithTelemetry(level, message, undefined, 'Console');
    };
  }

  debug(message: string, context?: any, component?: string): void {
    this.logWithTelemetry('debug', message, context, component);
  }

  info(message: string, context?: any, component?: string): void {
    this.logWithTelemetry('info', message, context, component);
  }

  warn(message: string, context?: any, component?: string): void {
    this.logWithTelemetry('warn', message, context, component);
  }

  error(message: string, context?: any, component?: string): void {
    this.logWithTelemetry('error', message, context, component);
  }

  logApiRequest(method: string, url: string, statusCode: number, duration: number, details?: any): void {
    const level = statusCode >= 400 ? 'error' : statusCode >= 300 ? 'warn' : 'info';
    const message = `API ${method} ${url} -> ${statusCode} (${duration}ms)`;
    
    this.logWithTelemetry(level, message, {
      method,
      url,
      statusCode,
      duration,
      ...details
    }, 'ApiClient');
  }

  logUserAction(action: string, component: string, details?: any): void {
    const message = `User action: ${action}`;
    
    this.logWithTelemetry('info', message, {
      action,
      component,
      userId: this.userId,
      ...details
    }, 'UserAction');
  }

  logSignalREvent(event: string, connectionState: string, details?: any): void {
    const level = connectionState === 'Connected' ? 'info' : 
                 connectionState === 'Reconnecting' ? 'warn' : 
                 connectionState === 'Disconnected' ? 'error' : 'info';
    
    const message = `SignalR ${event}: ${connectionState}`;
    
    this.logWithTelemetry(level, message, {
      event,
      connectionState,
      ...details
    }, 'SignalR');
  }

  logComponentLifecycle(component: string, phase: 'mount' | 'unmount' | 'update', details?: any): void {
    const message = `Component ${component} ${phase}`;
    
    this.logWithTelemetry('debug', message, {
      component,
      phase,
      ...details
    }, 'ComponentLifecycle');
  }

  private logWithTelemetry(level: LogEntry['level'], message: string, context?: any, component?: string): void {
    const span = this.tracer.startSpan(`log.${level}`);
    
    try {
      // Set span attributes
      span.setAttributes({
        'log.level': level,
        'log.message': message,
        'log.component': component || 'Unknown',
        'log.timestamp': new Date().toISOString(),
        'log.session_id': this.sessionId,
        'log.user_id': this.userId || 'anonymous',
      });

      if (context) {
        span.setAttributes({
          'log.context': JSON.stringify(context),
        });
      }

      // For error level, mark span as error
      if (level === 'error') {
        span.recordException(new Error(message));
        span.setStatus({ code: 2, message }); // ERROR status
      }

      // Add event to span
      span.addEvent('log_entry', {
        level,
        message,
        component: component || 'Unknown',
        context: context ? JSON.stringify(context) : undefined,
      });

    } finally {
      span.end();
    }

    // Store log entry for potential batching/local storage
    this.storeLogEntry({
      level,
      message,
      context,
      timestamp: new Date(),
      component,
      userId: this.userId,
      sessionId: this.sessionId,
    });
  }

  private storeLogEntry(entry: LogEntry): void {
    // Store in session storage for debugging (limit to last 1000 entries)
    try {
      const stored = sessionStorage.getItem('printfarmer_logs');
      const logs: LogEntry[] = stored ? JSON.parse(stored) : [];
      
      logs.push(entry);
      
      // Keep only last 1000 entries to prevent memory issues
      if (logs.length > 1000) {
        logs.splice(0, logs.length - 1000);
      }
      
      sessionStorage.setItem('printfarmer_logs', JSON.stringify(logs));
    } catch (error) {
      // If sessionStorage fails, use original console for the error
      this.originalConsole.error('[UnifiedLogging] Failed to store log entry:', error);
    }
  }

  // Debug helper methods
  getStoredLogs(): LogEntry[] {
    try {
      const stored = sessionStorage.getItem('printfarmer_logs');
      return stored ? JSON.parse(stored) : [];
    } catch {
      return [];
    }
  }

  clearStoredLogs(): void {
    try {
      sessionStorage.removeItem('printfarmer_logs');
    } catch (error) {
      this.originalConsole.error('[UnifiedLogging] Failed to clear stored logs:', error);
    }
  }

  exportLogs(): string {
    try {
      const logs = this.getStoredLogs();
      return JSON.stringify(logs, null, 2);
    } catch {
      return '[]';
    }
  }

  // Restore original console methods (for testing or cleanup)
  restoreConsole(): void {
    console.log = this.originalConsole.log;
    console.info = this.originalConsole.info;
    console.warn = this.originalConsole.warn;
    console.error = this.originalConsole.error;
    console.debug = this.originalConsole.debug;
  }
}

// Singleton instance
export const unifiedLogger = new UnifiedLoggingService();

// Extension methods for specific use cases
export const loggerExtensions = {
  // Printer-specific logging
  logPrinterOperation: (operation: string, printerId: string, success: boolean, details?: any) => {
    const level = success ? 'info' : 'warn';
    const message = `Printer ${operation}: ${success ? 'success' : 'failed'}`;
    unifiedLogger[level](message, { operation, printerId, success, ...details }, 'PrinterOperation');
  },

  // File operation logging
  logFileOperation: (operation: string, fileName: string, success: boolean, fileSize?: number, details?: any) => {
    const level = success ? 'info' : 'warn';
    const message = `File ${operation}: ${fileName}`;
    unifiedLogger[level](message, { operation, fileName, success, fileSize, ...details }, 'FileOperation');
  },

  // Navigation logging
  logNavigation: (from: string, to: string, details?: any) => {
    unifiedLogger.info(`Navigation: ${from} -> ${to}`, { from, to, ...details }, 'Navigation');
  },

  // Form submission logging
  logFormSubmission: (formName: string, success: boolean, validationErrors?: any, details?: any) => {
    const level = success ? 'info' : 'warn';
    const message = `Form ${formName}: ${success ? 'submitted' : 'validation failed'}`;
    unifiedLogger[level](message, { formName, success, validationErrors, ...details }, 'FormSubmission');
  },
};

export default unifiedLogger;