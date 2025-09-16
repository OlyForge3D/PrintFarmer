import { WebTracerProvider } from '@opentelemetry/sdk-trace-web';
import { getWebAutoInstrumentations } from '@opentelemetry/auto-instrumentations-web';
import { resourceFromAttributes } from '@opentelemetry/resources';
import { SEMRESATTRS_SERVICE_NAME, SEMRESATTRS_SERVICE_VERSION, SEMRESATTRS_SERVICE_NAMESPACE } from '@opentelemetry/semantic-conventions';
import { OTLPTraceExporter } from '@opentelemetry/exporter-trace-otlp-http';
import { BatchSpanProcessor, ConsoleSpanExporter } from '@opentelemetry/sdk-trace-web';
import { registerInstrumentations } from '@opentelemetry/instrumentation';
// Import unified logging to initialize console redirection
import '../services/unifiedLogging';

// Configuration for OpenTelemetry
const config = {
  serviceName: 'PrintFarmer.Frontend',
  serviceVersion: '1.0.0',
  // Configure OTLP endpoint - can be overridden by environment variables
  otlpEndpoint: import.meta.env.VITE_OTEL_EXPORTER_OTLP_ENDPOINT || '',
  otlpHeaders: import.meta.env.VITE_OTEL_EXPORTER_OTLP_HEADERS || '',
  enableConsoleLogging: import.meta.env.DEV === true
};

let provider: WebTracerProvider | null = null;

export function initializeTelemetry(): void {
  if (provider) {
    console.warn('OpenTelemetry SDK already initialized');
    return;
  }

  try {
    const resource = resourceFromAttributes({
      [SEMRESATTRS_SERVICE_NAME]: config.serviceName,
      [SEMRESATTRS_SERVICE_VERSION]: config.serviceVersion,
      [SEMRESATTRS_SERVICE_NAMESPACE]: 'printfarmer',
      'environment': import.meta.env.MODE,
      'platform': 'web'
    });

    // Configure span processors
    const spanProcessors: BatchSpanProcessor[] = [];
    
    // Add console exporter for development
    if (config.enableConsoleLogging) {
      spanProcessors.push(new BatchSpanProcessor(new ConsoleSpanExporter()));
    }

    // Add OTLP exporter if endpoint is configured
    if (config.otlpEndpoint) {
      const otlpExporter = new OTLPTraceExporter({
        url: config.otlpEndpoint,
        headers: config.otlpHeaders ? JSON.parse(config.otlpHeaders) : {}
      });
      spanProcessors.push(new BatchSpanProcessor(otlpExporter));
    }

    provider = new WebTracerProvider({
      resource,
      spanProcessors
    });

    // Register the provider globally
    provider.register();

    // Register auto-instrumentations
    registerInstrumentations({
      instrumentations: [
        getWebAutoInstrumentations({
          '@opentelemetry/instrumentation-document-load': {
            enabled: true,
          },
          '@opentelemetry/instrumentation-user-interaction': {
            enabled: true,
          },
          '@opentelemetry/instrumentation-fetch': {
            enabled: true,
            propagateTraceHeaderCorsUrls: [
              /^https?:\/\/localhost:5245\/.*/, // API server
              /^https?:\/\/.*printfarmer.*\/.*/ // Production domains
            ],
            clearTimingResources: true,
          },
          '@opentelemetry/instrumentation-xml-http-request': {
            enabled: true,
            propagateTraceHeaderCorsUrls: [
              /^https?:\/\/localhost:5245\/.*/, // API server
              /^https?:\/\/.*printfarmer.*\/.*/ // Production domains
            ],
          },
        })
      ]
    });

    console.log('[Telemetry] OpenTelemetry initialized successfully');
    console.log('[UnifiedLogging] Console redirection enabled - all console statements are now captured in OpenTelemetry');
  } catch (error) {
    console.error('[Telemetry] Failed to initialize OpenTelemetry:', error);
  }
}

export function shutdownTelemetry(): Promise<void> {
  if (!provider) {
    return Promise.resolve();
  }

  return provider.shutdown().then(() => {
    console.log('[Telemetry] OpenTelemetry shut down successfully');
    provider = null;
  }).catch((error: unknown) => {
    console.error('[Telemetry] Error shutting down OpenTelemetry:', error);
  });
}

// Utility function to check if telemetry is initialized
export function isTelemetryInitialized(): boolean {
  return provider !== null;
}