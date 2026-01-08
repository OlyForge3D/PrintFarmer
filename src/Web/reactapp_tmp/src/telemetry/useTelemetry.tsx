import { useCallback } from 'react';
import { trace, context, SpanStatusCode, SpanKind } from '@opentelemetry/api';

const tracer = trace.getTracer('PrintFarmer.Frontend');

export interface TelemetryOptions {
  component?: string;
  operation?: string;
  attributes?: Record<string, string | number | boolean>;
}

export function useTelemetry() {
  const startSpan = useCallback((name: string, options?: TelemetryOptions) => {
    const span = tracer.startSpan(name, {
      kind: SpanKind.INTERNAL,
      attributes: {
        'component.type': 'react',
        'component.name': options?.component || 'unknown',
        'operation': options?.operation || name,
        ...options?.attributes
      }
    });
    return span;
  }, []);

  const recordError = useCallback((span: ReturnType<typeof tracer.startSpan>, error: Error) => {
    span.recordException(error);
    span.setStatus({
      code: SpanStatusCode.ERROR,
      message: error.message
    });
  }, []);

  const trackComponentMount = useCallback((componentName: string, props?: Record<string, unknown>) => {
    const span = tracer.startSpan(`${componentName}.mount`, {
      kind: SpanKind.INTERNAL,
      attributes: {
        'component.type': 'react',
        'component.name': componentName,
        'component.lifecycle': 'mount',
        'props.count': props ? Object.keys(props).length : 0
      }
    });
    return span;
  }, []);

  const trackComponentUnmount = useCallback((componentName: string, span?: ReturnType<typeof tracer.startSpan>) => {
    if (span) {
      span.setAttributes({
        'component.lifecycle': 'unmount'
      });
      span.end();
    } else {
      // Create a span for unmount if mount wasn't tracked
      const unmountSpan = tracer.startSpan(`${componentName}.unmount`, {
        kind: SpanKind.INTERNAL,
        attributes: {
          'component.type': 'react',
          'component.name': componentName,
          'component.lifecycle': 'unmount'
        }
      });
      unmountSpan.end();
    }
  }, []);

  const trackAsyncOperation = useCallback(async function<T>(
    operationName: string,
    operation: () => Promise<T>,
    attributes?: Record<string, string | number | boolean>
  ): Promise<T> {
    const span = tracer.startSpan(operationName, {
      kind: SpanKind.INTERNAL,
      attributes: {
        'operation.type': 'async',
        ...attributes
      }
    });

    try {
      const result = await context.with(trace.setSpan(context.active(), span), operation);
      span.setStatus({ code: SpanStatusCode.OK });
      return result;
    } catch (error) {
      recordError(span, error as Error);
      throw error;
    } finally {
      span.end();
    }
  }, [recordError]);

  const trackUserInteraction = useCallback((action: string, target?: string, metadata?: Record<string, unknown>) => {
    const span = tracer.startSpan(`user.${action}`, {
      kind: SpanKind.INTERNAL,
      attributes: {
        'user.action': action,
        'user.target': target || 'unknown',
        'interaction.type': 'click',
        ...metadata
      }
    });
    span.end(); // User interactions are typically short-lived
  }, []);

  const trackApiCall = useCallback(async function<T>(
    endpoint: string,
    method: string,
    apiCall: () => Promise<T>
  ): Promise<T> {
    return trackAsyncOperation(
      `api.${method.toLowerCase()}.${endpoint.replace(/[/{}]/g, '_')}`,
      apiCall,
      {
        'http.method': method,
        'http.url': endpoint,
        'operation.type': 'http'
      }
    );
  }, [trackAsyncOperation]);

  return {
    startSpan,
    recordError,
    trackComponentMount,
    trackComponentUnmount,
    trackAsyncOperation,
    trackUserInteraction,
    trackApiCall
  };
}